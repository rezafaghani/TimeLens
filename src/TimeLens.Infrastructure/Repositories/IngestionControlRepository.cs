using System.Text.Json;
using TimeLens.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace TimeLens.Infrastructure.Repositories;

public class IngestionControlRepository(TimeLensContext context) : IIngestionControlRepository
{
    public async Task<List<IngestionSchedule>> GetEnabledSchedulesAsync(CancellationToken cancellationToken = default)
    {
        return await QuerySchedulesAsync("WHERE enabled = true", [], cancellationToken);
    }

    public async Task<List<IngestionSchedule>> GetSchedulesAsync(long? seriesId = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<NpgsqlParameter>();
        var where = "";
        if (seriesId.HasValue)
        {
            where = "WHERE series_id = @series_id";
            parameters.Add(new NpgsqlParameter("series_id", seriesId.Value));
        }

        return await QuerySchedulesAsync($"{where} ORDER BY series_id", parameters, cancellationToken);
    }

    public async Task<IngestionSchedule?> GetScheduleAsync(string id, CancellationToken cancellationToken = default)
    {
        var schedules = await QuerySchedulesAsync("WHERE id = @id LIMIT 1", [new NpgsqlParameter("id", id)], cancellationToken);
        return schedules.FirstOrDefault();
    }

    public async Task<List<IngestionJob>> GetJobsAsync(string? scheduleId, long? seriesId, CancellationToken cancellationToken = default)
    {
        var (where, parameters) = BuildWhere(("schedule_id", scheduleId), ("series_id", seriesId));
        var sql = $"""
            SELECT id, schedule_id, series_id, status, queued_at, started_at, finished_at, error
            FROM ingestion_jobs
            {where}
            ORDER BY queued_at DESC
            LIMIT 500
            """;

        await using var command = context.Postgres.CreateCommand(sql);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var jobs = new List<IngestionJob>();
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(new IngestionJob
            {
                Id = reader.GetString(0),
                ScheduleId = reader.GetString(1),
                SeriesId = reader.GetInt64(2),
                Status = reader.GetString(3),
                QueuedAt = reader.GetFieldValue<DateTimeOffset>(4),
                StartedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                FinishedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                Error = reader.GetString(7)
            });
        }

        return jobs;
    }

    public async Task<List<IngestionExecution>> GetExecutionsAsync(string? jobId, string? scheduleId, long? seriesId, CancellationToken cancellationToken = default)
    {
        var (where, parameters) = BuildWhere(("job_id", jobId), ("schedule_id", scheduleId), ("series_id", seriesId));
        var sql = $"""
            SELECT id, job_id, schedule_id, series_id, status, created_at, started_at, finished_at, inserted, skipped, error
            FROM ingestion_executions
            {where}
            ORDER BY created_at DESC
            LIMIT 500
            """;

        await using var command = context.Postgres.CreateCommand(sql);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var executions = new List<IngestionExecution>();
        while (await reader.ReadAsync(cancellationToken))
        {
            executions.Add(new IngestionExecution
            {
                Id = reader.GetString(0),
                JobId = reader.GetString(1),
                ScheduleId = reader.GetString(2),
                SeriesId = reader.GetInt64(3),
                Status = reader.GetString(4),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(5),
                StartedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                FinishedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                Inserted = reader.GetInt32(8),
                Skipped = reader.GetInt32(9),
                Error = reader.GetString(10)
            });
        }

        return executions;
    }

    public async Task EnsureDefaultSchedulesAsync(IEnumerable<IngestionSchedule> schedules, CancellationToken cancellationToken = default)
    {
        foreach (var schedule in schedules)
        {
            await using var command = context.Postgres.CreateCommand("""
                INSERT INTO ingestion_schedules (
                    id, series_id, name, cron_expression, default_cron_expression, enabled, source, endpoint, parameters,
                    lookback_hours, window_start_expression, window_end_expression, default_window_start_expression,
                    default_window_end_expression, batch_size, last_queued_at, created_at, updated_at)
                VALUES (
                    @id, @series_id, @name, @cron_expression, @default_cron_expression, @enabled, @source, @endpoint, @parameters,
                    @lookback_hours, @window_start_expression, @window_end_expression, @default_window_start_expression,
                    @default_window_end_expression, @batch_size, @last_queued_at, @created_at, @updated_at)
                ON CONFLICT (id) DO UPDATE SET
                    series_id = EXCLUDED.series_id,
                    name = EXCLUDED.name,
                    cron_expression = CASE WHEN ingestion_schedules.cron_expression = '* * * * *' THEN EXCLUDED.cron_expression ELSE ingestion_schedules.cron_expression END,
                    default_cron_expression = EXCLUDED.default_cron_expression,
                    source = EXCLUDED.source,
                    endpoint = EXCLUDED.endpoint,
                    parameters = EXCLUDED.parameters,
                    lookback_hours = CASE WHEN ingestion_schedules.cron_expression = '* * * * *' THEN EXCLUDED.lookback_hours ELSE ingestion_schedules.lookback_hours END,
                    default_window_start_expression = EXCLUDED.default_window_start_expression,
                    default_window_end_expression = EXCLUDED.default_window_end_expression,
                    batch_size = CASE WHEN ingestion_schedules.cron_expression = '* * * * *' THEN EXCLUDED.batch_size ELSE ingestion_schedules.batch_size END,
                    updated_at = EXCLUDED.updated_at
                """);
            AddScheduleParameters(command, schedule);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<IngestionSchedule> CreateScheduleAsync(IngestionSchedule schedule, CancellationToken cancellationToken = default)
    {
        var definition = CanonicalSeriesRegistry.ResolveSchedule(schedule.Source, schedule.Parameters);
        if (definition.SeriesId != schedule.SeriesId)
        {
            throw new InvalidOperationException(
                $"Schedule parameters resolve to canonical series ID {definition.SeriesId}, not {schedule.SeriesId}.");
        }

        var now = DateTimeOffset.UtcNow;
        schedule.Id = string.IsNullOrWhiteSpace(schedule.Id) ? Guid.NewGuid().ToString("N") : schedule.Id;
        schedule.DefaultCronExpression = string.IsNullOrWhiteSpace(schedule.DefaultCronExpression) ? schedule.CronExpression : schedule.DefaultCronExpression;
        schedule.DefaultWindowStartExpression = string.IsNullOrWhiteSpace(schedule.DefaultWindowStartExpression) ? schedule.WindowStartExpression : schedule.DefaultWindowStartExpression;
        schedule.DefaultWindowEndExpression = string.IsNullOrWhiteSpace(schedule.DefaultWindowEndExpression) ? schedule.WindowEndExpression : schedule.DefaultWindowEndExpression;
        schedule.CreatedAt = now;
        schedule.UpdatedAt = now;

        await using var command = context.Postgres.CreateCommand("""
            INSERT INTO ingestion_schedules (
                id, series_id, name, cron_expression, default_cron_expression, enabled, source, endpoint, parameters,
                lookback_hours, window_start_expression, window_end_expression, default_window_start_expression,
                default_window_end_expression, batch_size, last_queued_at, created_at, updated_at)
            VALUES (
                @id, @series_id, @name, @cron_expression, @default_cron_expression, @enabled, @source, @endpoint, @parameters,
                @lookback_hours, @window_start_expression, @window_end_expression, @default_window_start_expression,
                @default_window_end_expression, @batch_size, @last_queued_at, @created_at, @updated_at)
            """);
        AddScheduleParameters(command, schedule);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return schedule;
    }

    public async Task<IngestionSchedule?> UpdateScheduleAsync(IngestionSchedule schedule, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync("""
            UPDATE ingestion_schedules
            SET cron_expression = @cron_expression,
                enabled = @enabled,
                window_start_expression = @window_start_expression,
                window_end_expression = @window_end_expression,
                batch_size = @batch_size,
                updated_at = @updated_at
            WHERE id = @id
            """, cancellationToken,
            ("cron_expression", schedule.CronExpression),
            ("enabled", schedule.Enabled),
            ("window_start_expression", schedule.WindowStartExpression),
            ("window_end_expression", schedule.WindowEndExpression),
            ("batch_size", schedule.BatchSize),
            ("updated_at", now),
            ("id", schedule.Id));

        return await GetScheduleAsync(schedule.Id, cancellationToken);
    }

    public async Task<IngestionSchedule?> ResetScheduleAsync(string id, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync("""
            UPDATE ingestion_schedules
            SET cron_expression = NULLIF(default_cron_expression, ''),
                window_start_expression = default_window_start_expression,
                window_end_expression = default_window_end_expression,
                updated_at = @updated_at
            WHERE id = @id AND default_cron_expression <> ''
            """, cancellationToken, ("updated_at", now), ("id", id));

        return await GetScheduleAsync(id, cancellationToken);
    }

    public async Task<IngestionJobMessage?> TryCreateQueuedJobAsync(IngestionSchedule schedule, DateTimeOffset queuedAt, CancellationToken cancellationToken = default)
    {
        if (!await HasActiveDatasetAsync(schedule, cancellationToken))
        {
            return null;
        }

        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using var reserve = new NpgsqlCommand("""
            UPDATE ingestion_schedules
            SET last_queued_at = @queued_at, updated_at = @queued_at
            WHERE id = @id AND last_queued_at IS NOT DISTINCT FROM @last_queued_at
            """, connection, transaction);
        reserve.Parameters.AddWithValue("queued_at", queuedAt);
        reserve.Parameters.AddWithValue("id", schedule.Id);
        reserve.Parameters.AddWithValue("last_queued_at", DbValue.From(schedule.LastQueuedAt));

        if (await reserve.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var job = new IngestionJob
        {
            Id = Guid.NewGuid().ToString("N"),
            ScheduleId = schedule.Id,
            SeriesId = schedule.SeriesId,
            Status = IngestionStatuses.Queued,
            QueuedAt = queuedAt
        };
        var execution = new IngestionExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            JobId = job.Id,
            ScheduleId = schedule.Id,
            SeriesId = schedule.SeriesId,
            Status = IngestionStatuses.Queued,
            CreatedAt = queuedAt
        };

        await InsertJobAsync(connection, transaction, job, cancellationToken);
        await InsertExecutionAsync(connection, transaction, execution, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IngestionJobMessage
        {
            ScheduleId = schedule.Id,
            JobId = job.Id,
            ExecutionId = execution.Id,
            SeriesId = schedule.SeriesId,
            Source = schedule.Source,
            Endpoint = schedule.Endpoint,
            Parameters = schedule.Parameters,
            LookbackHours = schedule.LookbackHours,
            WindowStartExpression = schedule.WindowStartExpression,
            WindowEndExpression = schedule.WindowEndExpression,
            BatchSize = schedule.BatchSize
        };
    }

    public async Task<IngestionJobMessage> CreateBackloadJobAsync(
        IngestionSchedule schedule,
        string endpoint,
        string source,
        Dictionary<string, string> parameters,
        string windowStartExpression,
        string windowEndExpression,
        int batchSize,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var job = new IngestionJob
        {
            Id = Guid.NewGuid().ToString("N"),
            ScheduleId = schedule.Id,
            SeriesId = schedule.SeriesId,
            Status = IngestionStatuses.Queued,
            QueuedAt = queuedAt
        };
        var execution = new IngestionExecution
        {
            Id = Guid.NewGuid().ToString("N"),
            JobId = job.Id,
            ScheduleId = schedule.Id,
            SeriesId = schedule.SeriesId,
            Status = IngestionStatuses.Queued,
            CreatedAt = queuedAt
        };

        await InsertJobAsync(connection, transaction, job, cancellationToken);
        await InsertExecutionAsync(connection, transaction, execution, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new IngestionJobMessage
        {
            ScheduleId = schedule.Id,
            JobId = job.Id,
            ExecutionId = execution.Id,
            SeriesId = schedule.SeriesId,
            Source = source,
            Endpoint = endpoint,
            Parameters = parameters,
            LookbackHours = schedule.LookbackHours,
            WindowStartExpression = windowStartExpression,
            WindowEndExpression = windowEndExpression,
            BatchSize = batchSize
        };
    }

    public async Task<IngestionJobMessage?> TryCreateBackloadJobAsync(
        IngestionSchedule schedule,
        string endpoint,
        string source,
        Dictionary<string, string> parameters,
        string windowStartExpression,
        string windowEndExpression,
        int batchSize,
        string jobId,
        DateTimeOffset queuedAt,
        CancellationToken cancellationToken = default)
    {
        var executionId = $"{jobId}-execution";
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO ingestion_jobs (id, schedule_id, series_id, status, queued_at, started_at, finished_at, error)
                VALUES (@id, @schedule_id, @series_id, @status, @queued_at, NULL, NULL, '')
                ON CONFLICT (id) DO NOTHING
                """;
            command.Parameters.AddWithValue("id", jobId);
            command.Parameters.AddWithValue("schedule_id", schedule.Id);
            command.Parameters.AddWithValue("series_id", schedule.SeriesId);
            command.Parameters.AddWithValue("status", IngestionStatuses.Queued);
            command.Parameters.AddWithValue("queued_at", queuedAt);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                var existing = await GetJobsAsync(schedule.Id, schedule.SeriesId, cancellationToken);
                return existing.Any(job => job.Id == jobId && job.Status == IngestionStatuses.Queued)
                    ? CreateBackloadMessage()
                    : null;
            }
        }

        var execution = new IngestionExecution
        {
            Id = executionId,
            JobId = jobId,
            ScheduleId = schedule.Id,
            SeriesId = schedule.SeriesId,
            Status = IngestionStatuses.Queued,
            CreatedAt = queuedAt
        };
        await InsertExecutionAsync(connection, transaction, execution, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CreateBackloadMessage();

        IngestionJobMessage CreateBackloadMessage() => new()
        {
            ScheduleId = schedule.Id,
            JobId = jobId,
            ExecutionId = executionId,
            SeriesId = schedule.SeriesId,
            Source = source,
            Endpoint = endpoint,
            Parameters = parameters,
            LookbackHours = schedule.LookbackHours,
            WindowStartExpression = windowStartExpression,
            WindowEndExpression = windowEndExpression,
            BatchSize = batchSize
        };
    }

    public async Task MarkJobRunningAsync(string jobId, string executionId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync("""
            UPDATE ingestion_jobs SET status = @status, started_at = @now WHERE id = @job_id;
            UPDATE ingestion_executions SET status = @status, started_at = @now WHERE id = @execution_id;
            """, cancellationToken, ("status", IngestionStatuses.Running), ("now", now), ("job_id", jobId), ("execution_id", executionId));
    }

    public async Task MarkJobArtifactsQueuedAsync(
        string jobId,
        string executionId,
        int artifactCount,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync("""
            WITH updated_execution AS (
                UPDATE ingestion_executions
                SET artifacts_expected = GREATEST(artifacts_expected, @artifact_count),
                    status = CASE
                        WHEN status = @failed THEN status
                        WHEN artifacts_completed >= GREATEST(artifacts_expected, @artifact_count) THEN @completed
                        ELSE @queued
                    END,
                    finished_at = CASE
                        WHEN status = @failed THEN finished_at
                        WHEN artifacts_completed >= GREATEST(artifacts_expected, @artifact_count) THEN @now
                        ELSE NULL
                    END
                WHERE id = @execution_id
                RETURNING job_id, status, finished_at
            )
            UPDATE ingestion_jobs AS job
            SET status = updated_execution.status, finished_at = updated_execution.finished_at
            FROM updated_execution
            WHERE job.id = updated_execution.job_id AND job.id = @job_id AND job.status <> @failed;
            """, cancellationToken,
            ("artifact_count", artifactCount),
            ("failed", IngestionStatuses.Failed),
            ("completed", IngestionStatuses.Completed),
            ("queued", IngestionStatuses.Queued),
            ("now", now),
            ("execution_id", executionId),
            ("job_id", jobId));
    }

    public async Task MarkJobFailedAsync(string jobId, string executionId, string error, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync("""
            UPDATE ingestion_jobs SET status = @status, finished_at = @now, error = @error WHERE id = @job_id;
            UPDATE ingestion_executions SET status = @status, finished_at = @now, error = @error WHERE id = @execution_id;
            """, cancellationToken, ("status", IngestionStatuses.Failed), ("now", now), ("error", error), ("job_id", jobId), ("execution_id", executionId));
    }

    private async Task<List<IngestionSchedule>> QuerySchedulesAsync(string suffix, List<NpgsqlParameter> parameters, CancellationToken cancellationToken)
    {
        var sql = $"""
            SELECT id, series_id, name, cron_expression, enabled, source, endpoint, parameters,
                   lookback_hours, batch_size, last_queued_at, created_at, updated_at,
                   default_cron_expression, window_start_expression, window_end_expression,
                   default_window_start_expression, default_window_end_expression
            FROM ingestion_schedules
            {suffix}
            """;

        await using var command = context.Postgres.CreateCommand(sql);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var schedules = new List<IngestionSchedule>();
        while (await reader.ReadAsync(cancellationToken))
        {
            schedules.Add(new IngestionSchedule
            {
                Id = reader.GetString(0),
                SeriesId = reader.GetInt64(1),
                Name = reader.GetString(2),
                CronExpression = reader.GetString(3),
                Enabled = reader.GetBoolean(4),
                Source = reader.GetString(5),
                Endpoint = reader.GetString(6),
                Parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7)) ?? [],
                LookbackHours = reader.GetInt32(8),
                BatchSize = reader.GetInt32(9),
                LastQueuedAt = reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(12),
                DefaultCronExpression = reader.GetString(13),
                WindowStartExpression = reader.GetString(14),
                WindowEndExpression = reader.GetString(15),
                DefaultWindowStartExpression = reader.GetString(16),
                DefaultWindowEndExpression = reader.GetString(17)
            });
        }

        return schedules;
    }

    private static (string Where, List<NpgsqlParameter> Parameters) BuildWhere(params (string Name, object? Value)[] filters)
    {
        var clauses = new List<string>();
        var parameters = new List<NpgsqlParameter>();

        foreach (var (name, value) in filters)
        {
            if (value is null || value is string text && string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            clauses.Add($"{name} = @{name}");
            parameters.Add(new NpgsqlParameter(name, value));
        }

        return (clauses.Count == 0 ? "" : $"WHERE {string.Join(" AND ", clauses)}", parameters);
    }

    private static void AddScheduleParameters(NpgsqlCommand command, IngestionSchedule schedule)
    {
        command.Parameters.AddWithValue("id", schedule.Id);
        command.Parameters.AddWithValue("series_id", schedule.SeriesId);
        command.Parameters.AddWithValue("name", schedule.Name);
        command.Parameters.AddWithValue("cron_expression", schedule.CronExpression);
        command.Parameters.AddWithValue("default_cron_expression", string.IsNullOrWhiteSpace(schedule.DefaultCronExpression) ? schedule.CronExpression : schedule.DefaultCronExpression);
        command.Parameters.AddWithValue("enabled", schedule.Enabled);
        command.Parameters.AddWithValue("source", schedule.Source);
        command.Parameters.AddWithValue("endpoint", schedule.Endpoint);
        command.Parameters.Add(new NpgsqlParameter("parameters", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(schedule.Parameters) });
        command.Parameters.AddWithValue("lookback_hours", schedule.LookbackHours);
        command.Parameters.AddWithValue("window_start_expression", schedule.WindowStartExpression);
        command.Parameters.AddWithValue("window_end_expression", schedule.WindowEndExpression);
        command.Parameters.AddWithValue("default_window_start_expression", schedule.DefaultWindowStartExpression);
        command.Parameters.AddWithValue("default_window_end_expression", schedule.DefaultWindowEndExpression);
        command.Parameters.AddWithValue("batch_size", schedule.BatchSize);
        command.Parameters.AddWithValue("last_queued_at", DbValue.From(schedule.LastQueuedAt));
        command.Parameters.AddWithValue("created_at", schedule.CreatedAt);
        command.Parameters.AddWithValue("updated_at", schedule.UpdatedAt);
    }

    private async Task<bool> HasActiveDatasetAsync(IngestionSchedule schedule, CancellationToken cancellationToken)
    {
        await using var command = context.Postgres.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM market_data_series
                WHERE series_id = @series_id
                  AND provider = @source
                  AND endpoint = @endpoint
                  AND deprecated = false
                  AND request_parameters @> @parameters
            )
            """);
        command.Parameters.AddWithValue("series_id", schedule.SeriesId);
        command.Parameters.AddWithValue("source", schedule.Source);
        command.Parameters.AddWithValue("endpoint", schedule.Endpoint.TrimStart('/'));
        command.Parameters.Add(new NpgsqlParameter("parameters", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(schedule.Parameters) });

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task InsertJobAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IngestionJob job, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ingestion_jobs (id, schedule_id, series_id, status, queued_at, started_at, finished_at, error)
            VALUES (@id, @schedule_id, @series_id, @status, @queued_at, @started_at, @finished_at, @error)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", job.Id);
        command.Parameters.AddWithValue("schedule_id", job.ScheduleId);
        command.Parameters.AddWithValue("series_id", job.SeriesId);
        command.Parameters.AddWithValue("status", job.Status);
        command.Parameters.AddWithValue("queued_at", job.QueuedAt);
        command.Parameters.AddWithValue("started_at", DbValue.From(job.StartedAt));
        command.Parameters.AddWithValue("finished_at", DbValue.From(job.FinishedAt));
        command.Parameters.AddWithValue("error", job.Error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertExecutionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IngestionExecution execution, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO ingestion_executions (id, job_id, schedule_id, series_id, status, created_at, started_at, finished_at, inserted, skipped, error)
            VALUES (@id, @job_id, @schedule_id, @series_id, @status, @created_at, @started_at, @finished_at, @inserted, @skipped, @error)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", execution.Id);
        command.Parameters.AddWithValue("job_id", execution.JobId);
        command.Parameters.AddWithValue("schedule_id", execution.ScheduleId);
        command.Parameters.AddWithValue("series_id", execution.SeriesId);
        command.Parameters.AddWithValue("status", execution.Status);
        command.Parameters.AddWithValue("created_at", execution.CreatedAt);
        command.Parameters.AddWithValue("started_at", DbValue.From(execution.StartedAt));
        command.Parameters.AddWithValue("finished_at", DbValue.From(execution.FinishedAt));
        command.Parameters.AddWithValue("inserted", execution.Inserted);
        command.Parameters.AddWithValue("skipped", execution.Skipped);
        command.Parameters.AddWithValue("error", execution.Error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = context.Postgres.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
