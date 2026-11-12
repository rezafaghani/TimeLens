using System.Text.Json;
using TimeLens.Domain.Models;
using Npgsql;
using NpgsqlTypes;

namespace TimeLens.Infrastructure.Repositories;

public class ExecutionRepository(TimeLensContext context) : IExecutionRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<ExecutionDefinitionDto>> GetDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand(BuildDefinitionSelectSql());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadDefinitions(reader, cancellationToken);
    }

    public async Task<List<ExecutionDefinitionDto>> GetEnabledDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand(BuildDefinitionSelectSql("d.enabled = true"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadDefinitions(reader, cancellationToken);
    }

    public async Task<ExecutionDefinitionDto?> GetDefinitionAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand(BuildDefinitionSelectSql("d.id = @id", orderBy: false));
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return (await ReadDefinitions(reader, cancellationToken)).SingleOrDefault();
    }

    public async Task<ExecutionDefinitionDto> UpsertDefinitionAsync(UpsertExecutionDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        var id = string.IsNullOrWhiteSpace(request.Id) ? $"execution-definition-{Guid.NewGuid():N}" : request.Id;
        var now = DateTimeOffset.UtcNow;
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution.execution_definitions (
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
            command.Parameters.AddWithValue("window_start_expression", request.WindowStartExpression?.Trim() ?? "now-24h");
            command.Parameters.AddWithValue("window_end_expression", request.WindowEndExpression?.Trim() ?? "now");
            command.Parameters.AddWithValue("max_parallelism", Math.Clamp(request.MaxParallelism ?? 4, 1, 32));
            command.Parameters.AddWithValue("timeout_seconds", Math.Clamp(request.TimeoutSeconds ?? 300, 30, 3600));
            AddJson(command, "tags", request.Tags);
            command.Parameters.AddWithValue("created_at", now);
            command.Parameters.AddWithValue("updated_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await ReplaceChildren(connection, transaction, id, request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetDefinitionAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Execution definition '{id}' was not saved.");
    }

    public async Task<ExecutionDefinitionDto?> SetDefinitionEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            UPDATE execution.execution_definitions
            SET enabled = @enabled, updated_at = @updated_at
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("enabled", enabled);
        command.Parameters.AddWithValue("updated_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return await GetDefinitionAsync(id, cancellationToken);
    }

    public async Task MarkDefinitionQueuedAsync(string id, DateTimeOffset queuedAt, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand("""
            UPDATE execution.execution_definitions
            SET last_queued_at = @last_queued_at, updated_at = @updated_at
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("last_queued_at", queuedAt);
        command.Parameters.AddWithValue("updated_at", queuedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ExecutionRunDto> SaveRunAsync(string definitionId, string triggerType, DateTimeOffset start, DateTimeOffset end, List<ExecutionStepResultDto> results, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = $"execution-run-{Guid.NewGuid():N}";
        var status = results.Any(x => x.Status == ExecutionRunStatuses.ProviderMissingData)
            ? ExecutionRunStatuses.ProviderMissingData
            : results.Any(x => x.Status == QualityStatuses.Critical)
                ? ExecutionRunStatuses.Failed
                : results.Count == 0
                    ? ExecutionRunStatuses.Passed
                    : ExecutionRunStatuses.Warning;
        await using var connection = await context.Postgres.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution.execution_runs (
                    id, definition_id, trigger_type, status, queued_at, started_at, finished_at,
                    evaluated_start, evaluated_end, target_count, completed_count, finding_count, critical_count, error)
                VALUES (
                    @id, @definition_id, @trigger_type, @status, @now, @now, @now,
                    @evaluated_start, @evaluated_end, @target_count, @completed_count, @finding_count, @critical_count, '')
                """;
            command.Parameters.AddWithValue("id", runId);
            command.Parameters.AddWithValue("definition_id", definitionId);
            command.Parameters.AddWithValue("trigger_type", triggerType);
            command.Parameters.AddWithValue("status", status);
            command.Parameters.AddWithValue("now", now);
            command.Parameters.AddWithValue("evaluated_start", start);
            command.Parameters.AddWithValue("evaluated_end", end);
            command.Parameters.AddWithValue("target_count", results.Select(x => x.TargetId).Distinct().Count());
            command.Parameters.AddWithValue("completed_count", results.Select(x => x.TargetId).Distinct().Count());
            command.Parameters.AddWithValue("finding_count", results.Count);
            command.Parameters.AddWithValue("critical_count", results.Count(x => x.Status == QualityStatuses.Critical || x.Status == ExecutionRunStatuses.Failed));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var result in results)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution.execution_results (
                    id, run_id, plugin_id, target_id, status, result_type, summary, metrics, payload, created_at)
                VALUES (
                    @id, @run_id, @plugin_id, @target_id, @status, @result_type, @summary, @metrics, @payload, @created_at)
                """;
            command.Parameters.AddWithValue("id", $"execution-result-{Guid.NewGuid():N}");
            command.Parameters.AddWithValue("run_id", runId);
            command.Parameters.AddWithValue("plugin_id", result.PluginId);
            command.Parameters.AddWithValue("target_id", result.TargetId);
            command.Parameters.AddWithValue("status", result.Status);
            command.Parameters.AddWithValue("result_type", result.ResultType);
            command.Parameters.AddWithValue("summary", result.Summary);
            AddJson(command, "metrics", result.Metrics);
            AddJson(command, "payload", result.Payload);
            command.Parameters.AddWithValue("created_at", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ExecutionRunDto(runId, definitionId, triggerType, status, now, now, now, start, end, results.Select(x => x.TargetId).Distinct().Count(), results.Select(x => x.TargetId).Distinct().Count(), results.Count, results.Count(x => x.Status == QualityStatuses.Critical || x.Status == ExecutionRunStatuses.Failed), string.Empty);
    }

    public async Task<ExecutionRunDto> SaveFailedRunAsync(string definitionId, string triggerType, DateTimeOffset? start, DateTimeOffset? end, string error, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var runId = $"execution-run-{Guid.NewGuid():N}";
        await using var command = context.Postgres.CreateCommand("""
            INSERT INTO execution.execution_runs (
                id, definition_id, trigger_type, status, queued_at, started_at, finished_at,
                evaluated_start, evaluated_end, target_count, completed_count, finding_count, critical_count, error)
            VALUES (
                @id, @definition_id, @trigger_type, @status, @now, @now, @now,
                @evaluated_start, @evaluated_end, 0, 0, 0, 1, @error)
            """);
        command.Parameters.AddWithValue("id", runId);
        command.Parameters.AddWithValue("definition_id", definitionId);
        command.Parameters.AddWithValue("trigger_type", triggerType);
        command.Parameters.AddWithValue("status", ExecutionRunStatuses.ExecutionError);
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("evaluated_start", start.HasValue ? start.Value : DBNull.Value);
        command.Parameters.AddWithValue("evaluated_end", end.HasValue ? end.Value : DBNull.Value);
        command.Parameters.AddWithValue("error", error);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new ExecutionRunDto(runId, definitionId, triggerType, ExecutionRunStatuses.ExecutionError, now, now, now, start, end, 0, 0, 0, 1, error);
    }

    public async Task<List<ExecutionRunDto>> GetRunsAsync(string? definitionId, CancellationToken cancellationToken = default)
    {
        await using var command = context.Postgres.CreateCommand();
        command.CommandText = $"""
            SELECT id, definition_id, trigger_type, status, queued_at, started_at, finished_at,
                   evaluated_start, evaluated_end, target_count, completed_count, finding_count, critical_count, error
            FROM execution.execution_runs
            {(string.IsNullOrWhiteSpace(definitionId) ? "" : "WHERE definition_id = @definition_id")}
            ORDER BY queued_at DESC
            LIMIT 500
            """;
        if (!string.IsNullOrWhiteSpace(definitionId))
        {
            command.Parameters.AddWithValue("definition_id", definitionId);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ExecutionRunDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRun(reader));
        }

        return result;
    }

    private static async Task ReplaceChildren(NpgsqlConnection connection, NpgsqlTransaction transaction, string definitionId, UpsertExecutionDefinitionRequest request, CancellationToken cancellationToken)
    {
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM execution.execution_definition_targets WHERE definition_id = @definition_id;
                DELETE FROM execution.execution_definition_plugins WHERE definition_id = @definition_id;
                """;
            delete.Parameters.AddWithValue("definition_id", definitionId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var target in request.Targets)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution.execution_definition_targets (definition_id, target_type, target_id, rule)
                VALUES (@definition_id, @target_type, @target_id, @rule)
                """;
            command.Parameters.AddWithValue("definition_id", definitionId);
            command.Parameters.AddWithValue("target_type", target.TargetType.Trim());
            command.Parameters.AddWithValue("target_id", target.TargetId.Trim());
            AddJson(command, "rule", target.Rule);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var order = 0;
        foreach (var plugin in request.Plugins)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO execution.execution_definition_plugins (
                    id, definition_id, plugin_id, plugin_version, enabled, configuration, severity, sort_order)
                VALUES (
                    @id, @definition_id, @plugin_id, @plugin_version, @enabled, @configuration, @severity, @sort_order)
                """;
            command.Parameters.AddWithValue("id", string.IsNullOrWhiteSpace(plugin.Id) ? $"execution-plugin-{Guid.NewGuid():N}" : plugin.Id);
            command.Parameters.AddWithValue("definition_id", definitionId);
            command.Parameters.AddWithValue("plugin_id", plugin.PluginId.Trim());
            command.Parameters.AddWithValue("plugin_version", plugin.PluginVersion ?? 1);
            command.Parameters.AddWithValue("enabled", plugin.Enabled);
            AddJson(command, "configuration", plugin.Configuration);
            AddJson(command, "severity", plugin.Severity);
            command.Parameters.AddWithValue("sort_order", plugin.SortOrder ?? order++);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private const string DefinitionSelectSql = """
        SELECT
            d.id, d.name, d.description, d.enabled, d.cron_expression, d.time_zone,
            d.window_start_expression, d.window_end_expression, d.max_parallelism, d.timeout_seconds,
            d.tags::text, d.created_at, d.updated_at, d.last_queued_at,
            COALESCE(jsonb_agg(DISTINCT jsonb_build_object('targetType', t.target_type, 'targetId', t.target_id, 'rule', t.rule))
                FILTER (WHERE t.definition_id IS NOT NULL), '[]'::jsonb)::text,
            COALESCE(jsonb_agg(DISTINCT jsonb_build_object('id', p.id, 'pluginId', p.plugin_id, 'pluginVersion', p.plugin_version,
                'enabled', p.enabled, 'configuration', p.configuration, 'severity', p.severity, 'sortOrder', p.sort_order))
                FILTER (WHERE p.definition_id IS NOT NULL), '[]'::jsonb)::text
        FROM execution.execution_definitions d
        LEFT JOIN execution.execution_definition_targets t ON t.definition_id = d.id
        LEFT JOIN execution.execution_definition_plugins p ON p.definition_id = d.id
        """;

    private static string BuildDefinitionSelectSql(string? where = null, bool orderBy = true) => $"""
        {DefinitionSelectSql}
        {(string.IsNullOrWhiteSpace(where) ? "" : $"WHERE {where}")}
        GROUP BY d.id
        {(orderBy ? "ORDER BY d.updated_at DESC" : "")}
        """;

    private static async Task<List<ExecutionDefinitionDto>> ReadDefinitions(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        var definitions = new List<ExecutionDefinitionDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            definitions.Add(new ExecutionDefinitionDto(
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
                JsonSerializer.Deserialize<List<ExecutionDefinitionTargetDto>>(reader.GetString(14), JsonOptions) ?? [],
                JsonSerializer.Deserialize<List<ExecutionDefinitionPluginDto>>(reader.GetString(15), JsonOptions) ?? [],
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13)));
        }

        return definitions;
    }

    private static ExecutionRunDto ReadRun(NpgsqlDataReader reader) => new(
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
        reader.GetString(13));

    private static void AddJson(NpgsqlCommand command, string name, JsonElement? value)
    {
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Jsonb)
        {
            Value = value.HasValue ? JsonSerializer.Serialize(value.Value, JsonOptions) : "{}"
        });
    }

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
}
