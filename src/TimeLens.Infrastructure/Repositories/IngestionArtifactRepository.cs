using Npgsql;
using TimeLens.Domain.Models;

namespace TimeLens.Infrastructure.Repositories;

public enum IngestionArtifactDisposition
{
    Process,
    Duplicate,
    AlreadyProcessed,
    InProgress
}

public sealed record IngestionArtifactClaim(IngestionArtifactDisposition Disposition, int Attempts, Guid? DuplicateOf = null);

public class IngestionArtifactRepository(TimeLensContext context)
{
    public async Task<IngestionArtifactClaim> TryBeginAsync(
        IngestionArtifactMessage message,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        await using (var insert = new NpgsqlCommand("""
            INSERT INTO ingestion_artifacts (
                id, job_id, execution_id, series_id, provider_series_id, ingested_at, raw_object_key,
                parsed_object_key, parsed_sha256, source_metadata_version, point_count, artifact_count, status, created_at)
            VALUES (
                @id, @job_id, @execution_id, @series_id, @provider_series_id, @ingested_at, @raw_object_key,
                @parsed_object_key, @parsed_sha256, @source_metadata_version, @point_count, @artifact_count, 'pending', @created_at)
            ON CONFLICT (id) DO NOTHING
            """, connection, transaction))
        {
            AddMessageParameters(insert, message);
            insert.Parameters.AddWithValue("created_at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        string status;
        int attempts;
        await using (var select = new NpgsqlCommand("""
            SELECT status, job_id, execution_id, series_id, provider_series_id, parsed_sha256, point_count, artifact_count, attempts,
                   (ingested_at = @ingested_at) AS ingested_at_matches,
                   raw_object_key, parsed_object_key, source_metadata_version
            FROM ingestion_artifacts
            WHERE id = @id
            FOR UPDATE
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("id", message.IngestionId);
            select.Parameters.AddWithValue("ingested_at", message.IngestedAt);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException($"Ingestion artifact {message.IngestionId:D} could not be recorded.");
            }

            status = reader.GetString(0);
            if (reader.GetString(1) != message.JobId
                || reader.GetString(2) != message.ExecutionId
                || reader.GetInt64(3) != message.SeriesId
                || reader.GetString(4) != message.ProviderSeriesId
                || reader.GetString(5) != message.ParsedSha256
                || reader.GetInt32(6) != message.PointCount
                || reader.GetInt32(7) != message.ArtifactCount
                || !reader.GetBoolean(9)
                || reader.GetString(10) != message.RawObjectKey
                || reader.GetString(11) != message.ParsedObjectKey
                || reader.GetString(12) != message.SourceMetadataVersion)
            {
                throw new InvalidOperationException($"Ingestion artifact {message.IngestionId:D} was received with conflicting metadata.");
            }

            attempts = reader.GetInt32(8);
        }

        await using (var updateExecution = new NpgsqlCommand("""
            UPDATE ingestion_executions
            SET artifacts_expected = GREATEST(artifacts_expected, @artifact_count)
            WHERE id = @execution_id
            """, connection, transaction))
        {
            updateExecution.Parameters.AddWithValue("artifact_count", message.ArtifactCount);
            updateExecution.Parameters.AddWithValue("execution_id", message.ExecutionId);
            await updateExecution.ExecuteNonQueryAsync(cancellationToken);
        }

        if (status is "inserted" or "duplicate")
        {
            await transaction.CommitAsync(cancellationToken);
            return new IngestionArtifactClaim(IngestionArtifactDisposition.AlreadyProcessed, attempts);
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE ingestion_artifacts
            SET status = 'processing', attempts = attempts + 1, error = '',
                started_at = COALESCE(started_at, @now), finished_at = NULL
            WHERE id = @id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("now", now);
            update.Parameters.AddWithValue("id", message.IngestionId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertHash = new NpgsqlCommand("""
            INSERT INTO ingestion_artifact_hashes (series_id, parsed_sha256, ingestion_id, status, updated_at)
            VALUES (@series_id, @parsed_sha256, @id, 'processing', @now)
            ON CONFLICT (series_id, parsed_sha256) DO NOTHING
            """, connection, transaction))
        {
            insertHash.Parameters.AddWithValue("series_id", message.SeriesId);
            insertHash.Parameters.AddWithValue("parsed_sha256", message.ParsedSha256);
            insertHash.Parameters.AddWithValue("id", message.IngestionId);
            insertHash.Parameters.AddWithValue("now", now);
            await insertHash.ExecuteNonQueryAsync(cancellationToken);
        }

        Guid ownerId;
        string hashStatus;
        await using (var selectHash = new NpgsqlCommand("""
            SELECT ingestion_id, status
            FROM ingestion_artifact_hashes
            WHERE series_id = @series_id AND parsed_sha256 = @parsed_sha256
            FOR UPDATE
            """, connection, transaction))
        {
            selectHash.Parameters.AddWithValue("series_id", message.SeriesId);
            selectHash.Parameters.AddWithValue("parsed_sha256", message.ParsedSha256);
            await using var reader = await selectHash.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The ingestion artifact hash claim could not be recorded.");
            }

            ownerId = reader.GetGuid(0);
            hashStatus = reader.GetString(1);
        }

        var nextAttempts = attempts + 1;
        if (ownerId == message.IngestionId)
        {
            await RefreshClaimAsync(connection, transaction, message, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IngestionArtifactClaim(IngestionArtifactDisposition.Process, nextAttempts);
        }

        if (hashStatus == "inserted")
        {
            await MarkDuplicateAsync(connection, transaction, message, ownerId, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new IngestionArtifactClaim(IngestionArtifactDisposition.Duplicate, nextAttempts, ownerId);
        }

        if (hashStatus == "processing")
        {
            await transaction.CommitAsync(cancellationToken);
            return new IngestionArtifactClaim(IngestionArtifactDisposition.InProgress, nextAttempts, ownerId);
        }

        await using (var takeOver = new NpgsqlCommand("""
            UPDATE ingestion_artifact_hashes
            SET ingestion_id = @id, status = 'processing', updated_at = @now
            WHERE series_id = @series_id AND parsed_sha256 = @parsed_sha256
            """, connection, transaction))
        {
            takeOver.Parameters.AddWithValue("id", message.IngestionId);
            takeOver.Parameters.AddWithValue("now", now);
            takeOver.Parameters.AddWithValue("series_id", message.SeriesId);
            takeOver.Parameters.AddWithValue("parsed_sha256", message.ParsedSha256);
            await takeOver.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new IngestionArtifactClaim(IngestionArtifactDisposition.Process, nextAttempts);
    }

    public async Task MarkCompletedAsync(
        IngestionArtifactMessage message,
        int inserted,
        int skipped,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? executionId = null;

        await using (var update = new NpgsqlCommand("""
            UPDATE ingestion_artifacts
            SET status = 'inserted', inserted_count = @inserted, skipped_count = @skipped,
                error = '', finished_at = @finished_at
            WHERE id = @id AND status = 'processing'
            RETURNING execution_id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("inserted", inserted);
            update.Parameters.AddWithValue("skipped", skipped);
            update.Parameters.AddWithValue("finished_at", DateTimeOffset.UtcNow);
            update.Parameters.AddWithValue("id", message.IngestionId);
            executionId = (string?)await update.ExecuteScalarAsync(cancellationToken);
        }

        if (executionId is not null)
        {
            await using (var updateHash = new NpgsqlCommand("""
                UPDATE ingestion_artifact_hashes
                SET status = 'inserted', updated_at = @updated_at
                WHERE series_id = @series_id AND parsed_sha256 = @parsed_sha256 AND ingestion_id = @id
                """, connection, transaction))
            {
                updateHash.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
                updateHash.Parameters.AddWithValue("series_id", message.SeriesId);
                updateHash.Parameters.AddWithValue("parsed_sha256", message.ParsedSha256);
                updateHash.Parameters.AddWithValue("id", message.IngestionId);
                await updateHash.ExecuteNonQueryAsync(cancellationToken);
            }

            await AddExecutionCountsAsync(connection, transaction, executionId, inserted, skipped, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid ingestionId,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? seriesId = null;
        string? parsedHash = null;

        await using (var update = new NpgsqlCommand("""
            UPDATE ingestion_artifacts
            SET status = 'failed', error = @error, finished_at = @finished_at
            WHERE id = @id AND status = 'processing'
            RETURNING series_id, parsed_sha256
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("error", error);
            update.Parameters.AddWithValue("finished_at", DateTimeOffset.UtcNow);
            update.Parameters.AddWithValue("id", ingestionId);
            await using var reader = await update.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                seriesId = reader.GetString(0);
                parsedHash = reader.GetString(1);
            }
        }

        if (seriesId is not null && parsedHash is not null)
        {
            await using var updateHash = new NpgsqlCommand("""
                UPDATE ingestion_artifact_hashes
                SET status = 'failed', updated_at = @updated_at
                WHERE series_id = @series_id AND parsed_sha256 = @parsed_sha256 AND ingestion_id = @id
                """, connection, transaction);
            updateHash.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
            updateHash.Parameters.AddWithValue("series_id", seriesId);
            updateHash.Parameters.AddWithValue("parsed_sha256", parsedHash);
            updateHash.Parameters.AddWithValue("id", ingestionId);
            await updateHash.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task MarkDuplicateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionArtifactMessage message,
        Guid duplicateOf,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string? executionId;
        await using (var update = new NpgsqlCommand("""
            UPDATE ingestion_artifacts
            SET status = 'duplicate', duplicate_of = @duplicate_of, skipped_count = point_count,
                finished_at = @finished_at
            WHERE id = @id
            RETURNING execution_id
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("duplicate_of", duplicateOf);
            update.Parameters.AddWithValue("finished_at", now);
            update.Parameters.AddWithValue("id", message.IngestionId);
            executionId = (string?)await update.ExecuteScalarAsync(cancellationToken);
        }

        if (executionId is not null)
        {
            await AddExecutionCountsAsync(connection, transaction, executionId, 0, message.PointCount, cancellationToken);
        }
    }

    private static async Task AddExecutionCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string executionId,
        int inserted,
        int skipped,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand("""
            UPDATE ingestion_executions
            SET inserted = inserted + @inserted,
                skipped = skipped + @skipped,
                artifacts_completed = artifacts_completed + 1,
                status = CASE
                    WHEN status = 'failed' THEN status
                    WHEN artifacts_expected > 0 AND artifacts_completed + 1 >= artifacts_expected THEN 'completed'
                    ELSE status
                END,
                finished_at = CASE
                    WHEN status <> 'failed' AND artifacts_expected > 0 AND artifacts_completed + 1 >= artifacts_expected
                    THEN @finished_at
                    ELSE finished_at
                END
            WHERE id = @execution_id
            RETURNING job_id, status
            """, connection, transaction);
        update.Parameters.AddWithValue("inserted", inserted);
        update.Parameters.AddWithValue("skipped", skipped);
        update.Parameters.AddWithValue("finished_at", DateTimeOffset.UtcNow);
        update.Parameters.AddWithValue("execution_id", executionId);
        await using var reader = await update.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) && reader.GetString(1) == "completed")
        {
            var jobId = reader.GetString(0);
            await reader.DisposeAsync();
            await using var updateJob = new NpgsqlCommand("""
                UPDATE ingestion_jobs
                SET status = 'completed', finished_at = @finished_at
                WHERE id = @job_id AND status <> 'failed'
                """, connection, transaction);
            updateJob.Parameters.AddWithValue("finished_at", DateTimeOffset.UtcNow);
            updateJob.Parameters.AddWithValue("job_id", jobId);
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task RefreshClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IngestionArtifactMessage message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var update = new NpgsqlCommand("""
            UPDATE ingestion_artifact_hashes
            SET status = 'processing', updated_at = @now
            WHERE series_id = @series_id AND parsed_sha256 = @parsed_sha256 AND ingestion_id = @id
            """, connection, transaction);
        update.Parameters.AddWithValue("now", now);
        update.Parameters.AddWithValue("series_id", message.SeriesId);
        update.Parameters.AddWithValue("parsed_sha256", message.ParsedSha256);
        update.Parameters.AddWithValue("id", message.IngestionId);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddMessageParameters(NpgsqlCommand command, IngestionArtifactMessage message)
    {
        command.Parameters.AddWithValue("id", message.IngestionId);
        command.Parameters.AddWithValue("job_id", message.JobId);
        command.Parameters.AddWithValue("execution_id", message.ExecutionId);
        command.Parameters.AddWithValue("series_id", message.SeriesId);
        command.Parameters.AddWithValue("provider_series_id", message.ProviderSeriesId);
        command.Parameters.AddWithValue("ingested_at", message.IngestedAt);
        command.Parameters.AddWithValue("raw_object_key", message.RawObjectKey);
        command.Parameters.AddWithValue("parsed_object_key", message.ParsedObjectKey);
        command.Parameters.AddWithValue("parsed_sha256", message.ParsedSha256);
        command.Parameters.AddWithValue("source_metadata_version", message.SourceMetadataVersion);
        command.Parameters.AddWithValue("point_count", message.PointCount);
        command.Parameters.AddWithValue("artifact_count", message.ArtifactCount);
    }
}
