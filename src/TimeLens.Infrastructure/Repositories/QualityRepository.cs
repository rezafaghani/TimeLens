using System.Text.Json;
using TimeLens.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace TimeLens.Infrastructure.Repositories;

public class QualityRepository(TimeLensContext context) : IQualityRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<QualityValidatorTypeDto>> GetValidatorTypesAsync(ValidationPluginUsage usage, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            SELECT id, category, display_name, description, target_type, configuration_version, default_severity, configuration_schema::text
            FROM quality_validation_plugins
            WHERE (usage & @usage) = @usage
            ORDER BY category, id
            """);
        command.Parameters.AddWithValue("usage", (int)usage);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<QualityValidatorTypeDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new QualityValidatorTypeDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                Json(reader.GetString(7))));
        }

        return result;
    }

    public async Task UpsertValidatorTypesAsync(IEnumerable<RegisteredValidationPluginDto> validators, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var validator in validators)
        {
            await using var command = context.Postgres.CreateCommand("""
                INSERT INTO quality_validation_plugins (
                    id, category, display_name, description, target_type, configuration_version,
                    default_severity, configuration_schema, usage, registered_at, updated_at)
                VALUES (
                    @id, @category, @display_name, @description, @target_type, @configuration_version,
                    @default_severity, @configuration_schema, @usage, @registered_at, @updated_at)
                ON CONFLICT (id) DO UPDATE SET
                    category = EXCLUDED.category,
                    display_name = EXCLUDED.display_name,
                    description = EXCLUDED.description,
                    target_type = EXCLUDED.target_type,
                    configuration_version = EXCLUDED.configuration_version,
                    default_severity = EXCLUDED.default_severity,
                    configuration_schema = EXCLUDED.configuration_schema,
                    usage = EXCLUDED.usage,
                    updated_at = EXCLUDED.updated_at
                """);
            command.Parameters.AddWithValue("id", validator.Id);
            command.Parameters.AddWithValue("category", validator.Category);
            command.Parameters.AddWithValue("display_name", validator.DisplayName);
            command.Parameters.AddWithValue("description", validator.Description);
            command.Parameters.AddWithValue("target_type", validator.TargetType);
            command.Parameters.AddWithValue("configuration_version", validator.ConfigurationVersion);
            command.Parameters.AddWithValue("default_severity", validator.DefaultSeverity);
            AddJson(command, "configuration_schema", validator.ConfigurationSchema);
            command.Parameters.AddWithValue("usage", (int)validator.Usage);
            command.Parameters.AddWithValue("registered_at", validator.RegisteredAt == default ? now : validator.RegisteredAt);
            command.Parameters.AddWithValue("updated_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<List<QualitySeriesGroupDto>> GetSeriesGroupsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            SELECT id, name, description, group_type, enabled, rule::text, tags::text, created_at, updated_at
            FROM quality_series_groups
            ORDER BY name
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<QualitySeriesGroupDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadGroup(reader));
        }

        return result;
    }

    public async Task<QualitySeriesGroupDto> UpsertSeriesGroupAsync(UpsertQualitySeriesGroupRequest request, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"group-{Guid.NewGuid():N}" : request.Id;
        var now = DateTimeOffset.UtcNow;
        await using var command = context.Postgres.CreateCommand("""
            INSERT INTO quality_series_groups (id, name, description, group_type, enabled, rule, tags, created_at, updated_at)
            VALUES (@id, @name, @description, @group_type, @enabled, @rule, @tags, @created_at, @updated_at)
            ON CONFLICT (id) DO UPDATE SET
                name = EXCLUDED.name,
                description = EXCLUDED.description,
                group_type = EXCLUDED.group_type,
                enabled = EXCLUDED.enabled,
                rule = EXCLUDED.rule,
                tags = EXCLUDED.tags,
                updated_at = EXCLUDED.updated_at
            RETURNING id, name, description, group_type, enabled, rule::text, tags::text, created_at, updated_at
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name.Trim());
        command.Parameters.AddWithValue("description", request.Description?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("group_type", string.IsNullOrWhiteSpace(request.GroupType) ? "static" : request.GroupType.Trim());
        command.Parameters.AddWithValue("enabled", request.Enabled);
        AddJson(command, "rule", request.Rule);
        AddJson(command, "tags", request.Tags);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadGroup(reader);
    }

    public async Task<QualitySeriesGroupDto?> SetSeriesGroupEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            UPDATE quality_series_groups
            SET enabled = @enabled, updated_at = @updated_at
            WHERE id = @id
            RETURNING id, name, description, group_type, enabled, rule::text, tags::text, created_at, updated_at
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGroup(reader) : null;
    }

    public async Task<List<QualitySeriesGroupMemberDto>> GetSeriesGroupMembersAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            SELECT group_id, dataset_id, series_id, created_at
            FROM quality_series_group_members
            WHERE group_id = @group_id
            ORDER BY series_id, dataset_id
            """);
        command.Parameters.AddWithValue("group_id", groupId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<QualitySeriesGroupMemberDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadGroupMember(reader));
        }

        return result;
    }

    public async Task<List<QualitySeriesGroupMemberDto>> ReplaceSeriesGroupMembersAsync(string groupId, ReplaceQualitySeriesGroupMembersRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM quality_series_group_members WHERE group_id = @group_id";
            delete.Parameters.AddWithValue("group_id", groupId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var member in request.Members.DistinctBy(x => x.DatasetId))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO quality_series_group_members (group_id, dataset_id, series_id, created_at)
                VALUES (@group_id, @dataset_id, @series_id, @created_at)
                """;
            insert.Parameters.AddWithValue("group_id", groupId);
            insert.Parameters.AddWithValue("dataset_id", member.DatasetId);
            insert.Parameters.AddWithValue("series_id", member.SeriesId);
            insert.Parameters.AddWithValue("created_at", now);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetSeriesGroupMembersAsync(groupId, cancellationToken);
    }

    public async Task<List<QualityValidationJobDto>> GetJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand(BuildJobSelectSql());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadJobs(reader, cancellationToken);
    }

    public async Task<List<QualityValidationJobDto>> GetEnabledJobsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand(BuildJobSelectSql("j.enabled = true"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadJobs(reader, cancellationToken);
    }

    public async Task<QualityValidationJobDto?> GetJobAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand(BuildJobSelectSql("j.id = @id", orderBy: false));
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return (await ReadJobs(reader, cancellationToken)).SingleOrDefault();
    }

    public async Task<List<QualityExecutionDto>> GetExecutionsAsync(string? jobId, string? seriesId, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand();
        var clauses = new List<string>();
        AddFilter(command, clauses, "e.job_id", "job_id", jobId);
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            clauses.Add("""
                EXISTS (
                    SELECT 1
                    FROM quality_validation_target_executions t
                    WHERE t.execution_id = e.id AND t.series_id = @series_id
                )
                """);
            command.Parameters.AddWithValue("series_id", seriesId);
        }

        command.CommandText = $"""
            SELECT e.id, e.job_id, e.trigger_type, e.status, e.queued_at, e.started_at, e.finished_at,
                   e.evaluated_start, e.evaluated_end, e.target_count, e.completed_count, e.warning_count,
                   e.critical_count, e.technical_failure_count, e.error
            FROM quality_validation_executions e
            {(clauses.Count == 0 ? "" : $"WHERE {string.Join(" AND ", clauses)}")}
            ORDER BY e.queued_at DESC
            LIMIT 200
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<QualityExecutionDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new QualityExecutionDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetInt32(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetString(14)));
        }

        return result;
    }

    public async Task<QualityValidationJobDto> UpsertJobAsync(UpsertQualityValidationJobRequest request, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"quality-job-{Guid.NewGuid():N}" : request.Id;
        var now = DateTimeOffset.UtcNow;
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quality_validation_jobs (
                    id, name, description, enabled, cron_expression, time_zone, window_start_expression,
                    window_end_expression, max_parallelism, timeout_seconds, tags, created_at, updated_at)
                VALUES (
                    @id, @name, @description, @enabled, @cron_expression, @time_zone, @window_start_expression,
                    @window_end_expression, @max_parallelism, @timeout_seconds, @tags, @created_at, @updated_at)
                ON CONFLICT (id) DO UPDATE SET
                    name = EXCLUDED.name,
                    description = EXCLUDED.description,
                    enabled = EXCLUDED.enabled,
                    cron_expression = EXCLUDED.cron_expression,
                    time_zone = EXCLUDED.time_zone,
                    window_start_expression = EXCLUDED.window_start_expression,
                    window_end_expression = EXCLUDED.window_end_expression,
                    max_parallelism = EXCLUDED.max_parallelism,
                    timeout_seconds = EXCLUDED.timeout_seconds,
                    tags = EXCLUDED.tags,
                    updated_at = EXCLUDED.updated_at
                """;
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("name", request.Name.Trim());
            command.Parameters.AddWithValue("description", request.Description?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("enabled", request.Enabled);
            command.Parameters.AddWithValue("cron_expression", request.CronExpression.Trim());
            command.Parameters.AddWithValue("time_zone", string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone.Trim());
            command.Parameters.AddWithValue("window_start_expression", request.WindowStartExpression.Trim());
            command.Parameters.AddWithValue("window_end_expression", request.WindowEndExpression.Trim());
            command.Parameters.AddWithValue("max_parallelism", Math.Clamp(request.MaxParallelism ?? 4, 1, 32));
            command.Parameters.AddWithValue("timeout_seconds", Math.Clamp(request.TimeoutSeconds ?? 300, 30, 3600));
            AddJson(command, "tags", request.Tags);
            command.Parameters.AddWithValue("created_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceJobChildren(connection, transaction, id, request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetJobAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Quality validation job '{id}' was not saved.");
    }

    public async Task<QualityValidationJobDto?> SetJobEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            UPDATE quality_validation_jobs
            SET enabled = @enabled, updated_at = @updated_at
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetJobAsync(id, cancellationToken);
    }

    public async Task MarkJobQueuedAsync(string id, DateTimeOffset queuedAt, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            UPDATE quality_validation_jobs
            SET last_queued_at = @last_queued_at, updated_at = @updated_at
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("last_queued_at", queuedAt);
        command.Parameters.AddWithValue("updated_at", queuedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<List<QualityFindingDto>> GetFindingsAsync(string? datasetId, string? seriesId, bool activeOnly, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand();
        var clauses = new List<string>();
        AddFilter(command, clauses, "dataset_id", datasetId);
        AddFilter(command, clauses, "series_id", seriesId);
        if (activeOnly)
        {
            clauses.Add("active = true");
        }

        command.CommandText = $"""
            SELECT id, execution_id, target_execution_id, validator_execution_id, dataset_id, series_id,
                   validator_id, category, severity, quality_status, trading_impact, title, message,
                   affected_start, affected_end, expected_count, actual_count, affected_count,
                   sample_timestamps::text, details::text, fingerprint, active, created_at, updated_at
            FROM quality_findings
            {(clauses.Count == 0 ? "" : $"WHERE {string.Join(" AND ", clauses)}")}
            ORDER BY active DESC, severity DESC, updated_at DESC
            LIMIT 500
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<QualityFindingDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadFinding(reader));
        }

        return result;
    }

    public async Task<QualityStatusDto?> GetStatusAsync(string? datasetId, string? seriesId, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand();
        var clauses = new List<string>();
        AddFilter(command, clauses, "dataset_id", datasetId);
        AddFilter(command, clauses, "series_id", seriesId);

        command.CommandText = $"""
            SELECT dataset_id, series_id, overall_status, category_statuses::text, latest_execution_id, as_of
            FROM quality_status_snapshots
            {(clauses.Count == 0 ? "" : $"WHERE {string.Join(" AND ", clauses)}")}
            ORDER BY as_of DESC
            LIMIT 1
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new QualityStatusDto(reader.GetString(0), reader.GetString(1), reader.GetString(2), Json(reader.GetString(3)), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(5))
            : null;
    }

    public async Task<QualitySummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            SELECT
                count(*) FILTER (WHERE overall_status = 'healthy')::int,
                count(*) FILTER (WHERE overall_status = 'degraded')::int,
                count(*) FILTER (WHERE overall_status = 'critical')::int,
                count(*) FILTER (WHERE overall_status = 'unknown')::int,
                (SELECT count(*)::int FROM quality_findings WHERE active = true),
                (SELECT count(*)::int FROM quality_findings WHERE active = true AND severity = 'critical')
            FROM quality_status_snapshots
            """);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new QualitySummaryDto(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    public async Task<string> SaveManualEvaluationAsync(ManualQualityEvaluationResult result, CancellationToken cancellationToken = default)
    {
        return await SaveEvaluationAsync($"quality-execution-{Guid.NewGuid():N}", "manual", "manual", result, cancellationToken);
    }

    public async Task<string> SaveJobEvaluationAsync(string executionId, string jobId, string triggerType, ManualQualityEvaluationResult result, CancellationToken cancellationToken = default)
    {
        return await SaveEvaluationAsync(
            string.IsNullOrWhiteSpace(executionId) ? $"quality-execution-{Guid.NewGuid():N}" : executionId,
            jobId,
            string.IsNullOrWhiteSpace(triggerType) ? "scheduled" : triggerType,
            result,
            cancellationToken);
    }

    private async Task<string> SaveEvaluationAsync(string executionId, string jobId, string triggerType, ManualQualityEvaluationResult result, CancellationToken cancellationToken)
    {
        var targetExecutionId = $"quality-target-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quality_validation_executions (
                    id, job_id, trigger_type, status, queued_at, started_at, finished_at,
                    evaluated_start, evaluated_end, config_snapshot, target_snapshot, target_count,
                    completed_count, warning_count, critical_count, technical_failure_count, error)
                VALUES (
                    @id, @job_id, @trigger_type, @status, @now, @now, @now,
                    @evaluated_start, @evaluated_end, @config_snapshot, @target_snapshot, 1,
                    1, @warning_count, @critical_count, 0, '')
            """;
            command.Parameters.AddWithValue("id", executionId);
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("trigger_type", triggerType);
            command.Parameters.AddWithValue("status", result.Findings.Count == 0 ? QualityExecutionStatuses.Completed : QualityExecutionStatuses.CompletedWithFindings);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("evaluated_start", result.Start);
            command.Parameters.AddWithValue("evaluated_end", result.End);
            AddJson(command, "config_snapshot", JsonSerializer.SerializeToElement(new { trigger = "manual" }, JsonOptions));
            AddJson(command, "target_snapshot", JsonSerializer.SerializeToElement(new[] { result.Metadata.Id }, JsonOptions));
            command.Parameters.AddWithValue("warning_count", result.Findings.Count(x => x.Severity == "warning"));
            command.Parameters.AddWithValue("critical_count", result.Findings.Count(x => x.Severity == "critical"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quality_validation_target_executions (
                    id, execution_id, dataset_id, series_id, status, started_at, finished_at,
                    evaluated_start, evaluated_end, point_count, error)
                VALUES (
                    @id, @execution_id, @dataset_id, @series_id, @status, @now, @now,
                    @evaluated_start, @evaluated_end, @point_count, '')
                """;
            command.Parameters.AddWithValue("id", targetExecutionId);
            command.Parameters.AddWithValue("execution_id", executionId);
            command.Parameters.AddWithValue("dataset_id", result.Metadata.Id);
            command.Parameters.AddWithValue("series_id", result.Metadata.SeriesId);
            command.Parameters.AddWithValue("status", result.OverallStatus);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("evaluated_start", result.Start);
            command.Parameters.AddWithValue("evaluated_end", result.End);
            command.Parameters.AddWithValue("point_count", result.PointCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var finding in result.Findings)
        {
            await SaveFinding(connection, transaction, executionId, targetExecutionId, result.Metadata, finding, now, cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quality_status_snapshots (
                    dataset_id, series_id, overall_status, category_statuses, latest_execution_id, as_of)
                VALUES (
                    @dataset_id, @series_id, @overall_status, @category_statuses, @latest_execution_id, @as_of)
                """;
            command.Parameters.AddWithValue("dataset_id", result.Metadata.Id);
            command.Parameters.AddWithValue("series_id", result.Metadata.SeriesId);
            command.Parameters.AddWithValue("overall_status", result.OverallStatus);
            AddJson(command, "category_statuses", JsonSerializer.SerializeToElement(CategoryStatuses(result.Findings), JsonOptions));
            command.Parameters.AddWithValue("latest_execution_id", executionId);
            command.Parameters.AddWithValue("as_of", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return executionId;
    }

    private static async Task ReplaceJobChildren(NpgsqlConnection connection, NpgsqlTransaction transaction, string jobId, UpsertQualityValidationJobRequest request, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM quality_validation_job_targets WHERE job_id = @job_id;
                DELETE FROM quality_validation_job_checks WHERE job_id = @job_id;
                """;
            delete.Parameters.AddWithValue("job_id", jobId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var target in request.Targets)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quality_validation_job_targets (job_id, target_type, target_id, rule)
                VALUES (@job_id, @target_type, @target_id, @rule)
                """;
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("target_type", target.TargetType.Trim());
            command.Parameters.AddWithValue("target_id", target.TargetId.Trim());
            AddJson(command, "rule", target.Rule);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var order = 0;
        foreach (var check in request.Checks)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quality_validation_job_checks (
                    id, job_id, validator_id, validator_version, enabled, configuration, severity, sort_order)
                VALUES (
                    @id, @job_id, @validator_id, @validator_version, @enabled, @configuration, @severity, @sort_order)
                """;
            command.Parameters.AddWithValue("id", string.IsNullOrWhiteSpace(check.Id) ? $"quality-check-{Guid.NewGuid():N}" : check.Id);
            command.Parameters.AddWithValue("job_id", jobId);
            command.Parameters.AddWithValue("validator_id", check.ValidatorId.Trim());
            command.Parameters.AddWithValue("validator_version", check.ValidatorVersion ?? 1);
            command.Parameters.AddWithValue("enabled", check.Enabled);
            AddJson(command, "configuration", check.Configuration);
            AddJson(command, "severity", check.Severity);
            command.Parameters.AddWithValue("sort_order", check.SortOrder ?? order++);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task SaveFinding(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string executionId,
        string targetExecutionId,
        DatasetMetadataDto metadata,
        QualityFindingDraftDto finding,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var fingerprint = $"{metadata.Id}:{finding.ValidatorId}:{finding.AffectedStart:O}:{finding.AffectedEnd:O}";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO quality_findings (
                id, execution_id, target_execution_id, validator_execution_id, dataset_id, series_id,
                validator_id, category, severity, quality_status, trading_impact, title, message,
                affected_start, affected_end, expected_count, actual_count, affected_count,
                sample_timestamps, details, fingerprint, active, created_at, updated_at)
            VALUES (
                @id, @execution_id, @target_execution_id, NULL, @dataset_id, @series_id,
                @validator_id, @category, @severity, @quality_status, @trading_impact, @title, @message,
                @affected_start, @affected_end, @expected_count, @actual_count, @affected_count,
                @sample_timestamps, @details, @fingerprint, true, @created_at, @updated_at)
            """;
        command.Parameters.AddWithValue("id", $"quality-finding-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("execution_id", executionId);
        command.Parameters.AddWithValue("target_execution_id", targetExecutionId);
        command.Parameters.AddWithValue("dataset_id", metadata.Id);
        command.Parameters.AddWithValue("series_id", metadata.SeriesId);
        command.Parameters.AddWithValue("validator_id", finding.ValidatorId);
        command.Parameters.AddWithValue("category", finding.Category);
        command.Parameters.AddWithValue("severity", finding.Severity);
        command.Parameters.AddWithValue("quality_status", finding.QualityStatus);
        command.Parameters.AddWithValue("trading_impact", finding.Severity == "critical" ? "high" : "medium");
        command.Parameters.AddWithValue("title", finding.Title);
        command.Parameters.AddWithValue("message", finding.Message);
        AddNullable(command, "affected_start", finding.AffectedStart);
        AddNullable(command, "affected_end", finding.AffectedEnd);
        AddNullable(command, "expected_count", finding.ExpectedCount);
        AddNullable(command, "actual_count", finding.ActualCount);
        AddNullable(command, "affected_count", finding.AffectedCount);
        AddJson(command, "sample_timestamps", JsonSerializer.SerializeToElement(finding.SampleTimestamps, JsonOptions));
        AddJson(command, "details", finding.Details ?? JsonSerializer.SerializeToElement(new { }, JsonOptions));
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("created_at", now);
        command.Parameters.AddWithValue("updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Dictionary<string, string> CategoryStatuses(List<QualityFindingDraftDto> findings)
    {
        var statuses = new Dictionary<string, string>();
        foreach (var finding in findings)
        {
            statuses[finding.Category] = statuses.TryGetValue(finding.Category, out var current) && current == QualityStatuses.Critical
                ? current
                : finding.QualityStatus;
        }

        return statuses;
    }

    private const string JobSelectSql = """
        SELECT
            j.id, j.name, j.description, j.enabled, j.cron_expression, j.time_zone,
            j.window_start_expression, j.window_end_expression, j.max_parallelism, j.timeout_seconds,
            j.tags::text, j.created_at, j.updated_at, j.last_queued_at,
            COALESCE(jsonb_agg(DISTINCT jsonb_build_object('targetType', t.target_type, 'targetId', t.target_id, 'rule', t.rule))
                FILTER (WHERE t.job_id IS NOT NULL), '[]'::jsonb)::text,
            COALESCE(jsonb_agg(DISTINCT jsonb_build_object('id', c.id, 'validatorId', c.validator_id, 'validatorVersion', c.validator_version,
                'enabled', c.enabled, 'configuration', c.configuration, 'severity', c.severity, 'sortOrder', c.sort_order))
                FILTER (WHERE c.job_id IS NOT NULL), '[]'::jsonb)::text
        FROM quality_validation_jobs j
        LEFT JOIN quality_validation_job_targets t ON t.job_id = j.id
        LEFT JOIN quality_validation_job_checks c ON c.job_id = j.id
        """;

    private static string BuildJobSelectSql(string? where = null, bool orderBy = true) => $"""
        {JobSelectSql}
        {(string.IsNullOrWhiteSpace(where) ? "" : $"WHERE {where}")}
        GROUP BY j.id
        {(orderBy ? "ORDER BY j.updated_at DESC" : "")}
        """;

    private static async Task<List<QualityValidationJobDto>> ReadJobs(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var jobs = new List<QualityValidationJobDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(new QualityValidationJobDto(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                Json(reader.GetString(10)),
                JsonSerializer.Deserialize<List<QualityValidationJobTargetDto>>(reader.GetString(14), JsonOptions) ?? [],
                JsonSerializer.Deserialize<List<QualityValidationJobCheckDto>>(reader.GetString(15), JsonOptions) ?? [],
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)));
        }

        return jobs;
    }

    private static QualitySeriesGroupDto ReadGroup(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetBoolean(4),
        Json(reader.GetString(5)),
        Json(reader.GetString(6)),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8));

    private static QualitySeriesGroupMemberDto ReadGroupMember(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetFieldValue<DateTimeOffset>(3));

    private static QualityFindingDto ReadFinding(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        reader.GetString(8),
        reader.GetString(9),
        reader.GetString(10),
        reader.GetString(11),
        reader.GetString(12),
        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
        reader.IsDBNull(15) ? null : reader.GetInt32(15),
        reader.IsDBNull(16) ? null : reader.GetInt32(16),
        reader.IsDBNull(17) ? null : reader.GetInt32(17),
        Json(reader.GetString(18)),
        Json(reader.GetString(19)),
        reader.GetString(20),
        reader.GetBoolean(21),
        reader.GetFieldValue<DateTimeOffset>(22),
        reader.GetFieldValue<DateTimeOffset>(23));

    private static void AddFilter(NpgsqlCommand command, List<string> clauses, string column, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        clauses.Add($"{column} = @{column}");
        command.Parameters.AddWithValue(column, value);
    }

    private static void AddFilter(NpgsqlCommand command, List<string> clauses, string column, string parameter, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        clauses.Add($"{column} = @{parameter}");
        command.Parameters.AddWithValue(parameter, value);
    }

    private static void AddJson(NpgsqlCommand command, string name, JsonElement? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = value.HasValue ? JsonSerializer.Serialize(value.Value, JsonOptions) : "{}"
        });
    }

    private static void AddNullable<T>(NpgsqlCommand command, string name, T? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
}
