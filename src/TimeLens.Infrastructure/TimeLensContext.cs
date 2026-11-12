namespace TimeLens.Infrastructure;

using ClickHouse.Client.ADO;
using Npgsql;

public sealed class TimeLensContext
{
    private static readonly SemaphoreSlim ClickHouseMigrationLock = new(1, 1);
    private readonly NpgsqlDataSource? _postgres;

    public TimeLensContext(string? postgresConnectionString, string? clickHouseConnectionString)
    {
        _postgres = string.IsNullOrWhiteSpace(postgresConnectionString) ? null : NpgsqlDataSource.Create(postgresConnectionString);
        ClickHouseConnectionString = clickHouseConnectionString ?? string.Empty;
    }

    public NpgsqlDataSource Postgres => _postgres ?? throw new InvalidOperationException("Postgres connection string is not configured.");
    public string ClickHouseConnectionString { get; }

    public ClickHouseConnection CreateClickHouseConnection()
    {
        if (string.IsNullOrWhiteSpace(ClickHouseConnectionString))
        {
            throw new InvalidOperationException("ClickHouse connection string is not configured.");
        }

        return new ClickHouseConnection(ClickHouseConnectionString);
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_postgres is not null)
        {
            await EnsurePostgresSchemaAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(ClickHouseConnectionString))
        {
            await EnsureClickHouseSchemaAsync(cancellationToken);
        }
    }

    public async Task EnsurePostgresSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS ingestion_schedules (
                id text PRIMARY KEY,
                series_id bigint NOT NULL,
                name text NOT NULL,
                cron_expression text NOT NULL,
                default_cron_expression text NOT NULL DEFAULT '',
                enabled boolean NOT NULL,
                source text NOT NULL DEFAULT 'coinbase-exchange',
                endpoint text NOT NULL,
                parameters jsonb NOT NULL,
                lookback_hours integer NOT NULL,
                window_start_expression text NOT NULL DEFAULT 'now-48h',
                window_end_expression text NOT NULL DEFAULT 'now',
                default_window_start_expression text NOT NULL DEFAULT 'now-48h',
                default_window_end_expression text NOT NULL DEFAULT 'now',
                batch_size integer NOT NULL,
                last_queued_at timestamptz NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS canonical_series_registry (
                series_id bigint PRIMARY KEY,
                provider text NOT NULL,
                provider_series_id text NOT NULL,
                timeframe text NOT NULL,
                UNIQUE (provider, provider_series_id, timeframe)
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_schedules_enabled_series
                ON ingestion_schedules(enabled, series_id);

            CREATE TABLE IF NOT EXISTS ingestion_jobs (
                id text PRIMARY KEY,
                schedule_id text NOT NULL,
                series_id bigint NOT NULL,
                status text NOT NULL,
                queued_at timestamptz NOT NULL,
                started_at timestamptz NULL,
                finished_at timestamptz NULL,
                error text NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_jobs_schedule_series_queued
                ON ingestion_jobs(schedule_id, series_id, queued_at DESC);

            CREATE TABLE IF NOT EXISTS ingestion_executions (
                id text PRIMARY KEY,
                job_id text NOT NULL,
                schedule_id text NOT NULL,
                series_id bigint NOT NULL,
                status text NOT NULL,
                created_at timestamptz NOT NULL,
                started_at timestamptz NULL,
                finished_at timestamptz NULL,
                inserted integer NOT NULL,
                skipped integer NOT NULL,
                artifacts_expected integer NOT NULL DEFAULT 0,
                artifacts_completed integer NOT NULL DEFAULT 0,
                error text NOT NULL
            );

            ALTER TABLE ingestion_executions
                ADD COLUMN IF NOT EXISTS artifacts_expected integer NOT NULL DEFAULT 0;
            ALTER TABLE ingestion_executions
                ADD COLUMN IF NOT EXISTS artifacts_completed integer NOT NULL DEFAULT 0;

            CREATE INDEX IF NOT EXISTS ix_ingestion_executions_job_schedule_series_created
                ON ingestion_executions(job_id, schedule_id, series_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS ingestion_artifacts (
                id uuid PRIMARY KEY,
                job_id text NOT NULL,
                execution_id text NOT NULL,
                definition_id text NULL,
                series_id bigint NOT NULL,
                provider_series_id text NOT NULL,
                ingested_at timestamptz NOT NULL,
                raw_object_key text NOT NULL,
                parsed_object_key text NOT NULL,
                parsed_sha256 text NOT NULL,
                source_metadata_version text NOT NULL,
                point_count integer NOT NULL,
                artifact_count integer NOT NULL,
                status text NOT NULL,
                inserted_count integer NOT NULL DEFAULT 0,
                skipped_count integer NOT NULL DEFAULT 0,
                duplicate_of uuid NULL REFERENCES ingestion_artifacts(id),
                attempts integer NOT NULL DEFAULT 0,
                error text NOT NULL DEFAULT '',
                created_at timestamptz NOT NULL,
                started_at timestamptz NULL,
                finished_at timestamptz NULL
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_artifacts_series_ingested
                ON ingestion_artifacts(series_id, ingested_at DESC);

            CREATE INDEX IF NOT EXISTS ix_ingestion_artifacts_status_created
                ON ingestion_artifacts(status, created_at);

            CREATE TABLE IF NOT EXISTS ingestion_artifact_hashes (
                series_id bigint NOT NULL,
                parsed_sha256 text NOT NULL,
                ingestion_id uuid NOT NULL REFERENCES ingestion_artifacts(id),
                status text NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (series_id, parsed_sha256)
            );

            CREATE TABLE IF NOT EXISTS market_data_series (
                definition_id text NOT NULL,
                series_id bigint PRIMARY KEY,
                provider_series_id text NOT NULL,
                provider text NOT NULL,
                exchange text NOT NULL,
                symbol text NOT NULL,
                asset_class text NOT NULL,
                base_asset text NOT NULL,
                quote_asset text NOT NULL,
                market_data_type text NOT NULL,
                timeframe text NOT NULL,
                currency text NOT NULL,
                time_zone text NOT NULL,
                provider_instrument_id text NOT NULL,
                endpoint text NOT NULL,
                unit text NOT NULL,
                calendar text NOT NULL,
                license_info text NOT NULL,
                deprecated boolean NOT NULL,
                request_parameters jsonb NOT NULL,
                provider_metadata jsonb NOT NULL,
                user_metadata jsonb NOT NULL,
                first_available_at timestamptz NULL,
                last_available_at timestamptz NULL,
                first_observed_at timestamptz NOT NULL,
                last_ingested_at timestamptz NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_market_data_series_filters
                ON market_data_series(provider, exchange, symbol, asset_class, market_data_type, timeframe);

            CREATE TABLE IF NOT EXISTS execution_definitions (
                id text PRIMARY KEY,
                name text NOT NULL,
                description text NOT NULL,
                enabled boolean NOT NULL,
                cron_expression text NOT NULL,
                time_zone text NOT NULL,
                window_start_expression text NOT NULL,
                window_end_expression text NOT NULL,
                max_parallelism integer NOT NULL,
                timeout_seconds integer NOT NULL,
                tags jsonb NOT NULL,
                last_queued_at timestamptz NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_execution_definitions_enabled
                ON execution_definitions(enabled, updated_at DESC);

            CREATE TABLE IF NOT EXISTS execution_definition_targets (
                definition_id text NOT NULL REFERENCES execution_definitions(id) ON DELETE CASCADE,
                target_type text NOT NULL,
                target_id text NOT NULL,
                rule jsonb NOT NULL
            );

            CREATE TABLE IF NOT EXISTS execution_definition_plugins (
                id text PRIMARY KEY,
                definition_id text NOT NULL REFERENCES execution_definitions(id) ON DELETE CASCADE,
                plugin_id text NOT NULL,
                plugin_version integer NOT NULL,
                enabled boolean NOT NULL,
                configuration jsonb NOT NULL,
                severity jsonb NOT NULL,
                sort_order integer NOT NULL
            );

            CREATE TABLE IF NOT EXISTS execution_runs (
                id text PRIMARY KEY,
                definition_id text NOT NULL,
                trigger_type text NOT NULL,
                status text NOT NULL,
                queued_at timestamptz NOT NULL,
                started_at timestamptz NULL,
                finished_at timestamptz NULL,
                evaluated_start timestamptz NULL,
                evaluated_end timestamptz NULL,
                target_count integer NOT NULL,
                completed_count integer NOT NULL,
                finding_count integer NOT NULL,
                critical_count integer NOT NULL,
                error text NOT NULL
            );

            CREATE TABLE IF NOT EXISTS execution_results (
                id text PRIMARY KEY,
                run_id text NOT NULL REFERENCES execution_runs(id) ON DELETE CASCADE,
                plugin_id text NOT NULL,
                target_id text NOT NULL,
                status text NOT NULL,
                result_type text NOT NULL,
                summary text NOT NULL,
                metrics jsonb NOT NULL,
                payload jsonb NOT NULL,
                created_at timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_series_groups (
                id text PRIMARY KEY,
                name text NOT NULL,
                description text NOT NULL,
                group_type text NOT NULL,
                enabled boolean NOT NULL,
                rule jsonb NOT NULL,
                tags jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_series_group_members (
                group_id text NOT NULL REFERENCES quality_series_groups(id) ON DELETE CASCADE,
                dataset_id text NOT NULL,
                series_id bigint NOT NULL,
                created_at timestamptz NOT NULL,
                PRIMARY KEY (group_id, dataset_id)
            );

            CREATE TABLE IF NOT EXISTS quality_validation_plugins (
                id text PRIMARY KEY,
                category text NOT NULL,
                display_name text NOT NULL,
                description text NOT NULL,
                target_type text NOT NULL,
                configuration_version integer NOT NULL,
                default_severity text NOT NULL,
                configuration_schema jsonb NOT NULL,
                usage integer NOT NULL,
                registered_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_validation_templates (
                id text PRIMARY KEY,
                name text NOT NULL,
                description text NOT NULL,
                tags jsonb NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            INSERT INTO quality_validation_templates (id, name, description, tags, created_at, updated_at)
            VALUES ('default-validation-template', 'Default validation template', 'Default template for validation schedules.', '{}'::jsonb, now(), now())
            ON CONFLICT (id) DO NOTHING;

            CREATE TABLE IF NOT EXISTS quality_validation_jobs (
                id text PRIMARY KEY,
                template_id text NOT NULL DEFAULT 'default-validation-template' REFERENCES quality_validation_templates(id),
                name text NOT NULL,
                description text NOT NULL,
                enabled boolean NOT NULL,
                cron_expression text NOT NULL,
                time_zone text NOT NULL,
                window_start_expression text NOT NULL,
                window_end_expression text NOT NULL,
                max_parallelism integer NOT NULL,
                timeout_seconds integer NOT NULL,
                tags jsonb NOT NULL,
                last_queued_at timestamptz NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_validation_job_targets (
                job_id text NOT NULL REFERENCES quality_validation_jobs(id) ON DELETE CASCADE,
                target_type text NOT NULL,
                target_id text NOT NULL,
                rule jsonb NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_validation_job_checks (
                id text PRIMARY KEY,
                job_id text NOT NULL REFERENCES quality_validation_jobs(id) ON DELETE CASCADE,
                validator_id text NOT NULL,
                validator_version integer NOT NULL,
                enabled boolean NOT NULL,
                configuration jsonb NOT NULL,
                severity jsonb NOT NULL,
                sort_order integer NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_validation_executions (
                id text PRIMARY KEY,
                job_id text NOT NULL,
                trigger_type text NOT NULL,
                status text NOT NULL,
                queued_at timestamptz NOT NULL,
                started_at timestamptz NULL,
                finished_at timestamptz NULL,
                evaluated_start timestamptz NULL,
                evaluated_end timestamptz NULL,
                config_snapshot jsonb NOT NULL,
                target_snapshot jsonb NOT NULL,
                target_count integer NOT NULL,
                completed_count integer NOT NULL,
                warning_count integer NOT NULL,
                critical_count integer NOT NULL,
                technical_failure_count integer NOT NULL,
                error text NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_validation_target_executions (
                id text PRIMARY KEY,
                execution_id text NOT NULL REFERENCES quality_validation_executions(id) ON DELETE CASCADE,
                dataset_id text NOT NULL,
                series_id bigint NOT NULL,
                status text NOT NULL,
                started_at timestamptz NULL,
                finished_at timestamptz NULL,
                evaluated_start timestamptz NULL,
                evaluated_end timestamptz NULL,
                point_count integer NOT NULL,
                error text NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_validator_executions (
                id text PRIMARY KEY,
                target_execution_id text NOT NULL REFERENCES quality_validation_target_executions(id) ON DELETE CASCADE,
                validator_id text NOT NULL,
                status text NOT NULL,
                severity text NOT NULL,
                duration_ms integer NOT NULL,
                metrics jsonb NOT NULL,
                error text NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quality_findings (
                id text PRIMARY KEY,
                execution_id text NOT NULL,
                target_execution_id text NULL,
                validator_execution_id text NULL,
                dataset_id text NOT NULL,
                series_id bigint NOT NULL,
                validator_id text NOT NULL,
                category text NOT NULL,
                severity text NOT NULL,
                quality_status text NOT NULL,
                trading_impact text NOT NULL,
                title text NOT NULL,
                message text NOT NULL,
                affected_start timestamptz NULL,
                affected_end timestamptz NULL,
                expected_count integer NULL,
                actual_count integer NULL,
                affected_count integer NULL,
                sample_timestamps jsonb NOT NULL,
                details jsonb NOT NULL,
                fingerprint text NOT NULL,
                active boolean NOT NULL,
                created_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_quality_findings_active_series
                ON quality_findings(active, dataset_id, series_id, severity, updated_at DESC);

            CREATE TABLE IF NOT EXISTS quality_status_snapshots (
                dataset_id text NOT NULL,
                series_id bigint NOT NULL,
                overall_status text NOT NULL,
                category_statuses jsonb NOT NULL,
                latest_execution_id text NOT NULL,
                as_of timestamptz NOT NULL,
                PRIMARY KEY (dataset_id, as_of)
            );

            CREATE INDEX IF NOT EXISTS ix_quality_status_snapshots_series_latest
                ON quality_status_snapshots(dataset_id, series_id, as_of DESC);
            """;

        await using var command = Postgres.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var migrationLockConnection = await Postgres.OpenConnectionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(731946120519)", migrationLockConnection))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            await SeedCanonicalSeriesRegistryAsync(cancellationToken);
            await MigrateMarketDataSeriesAsync(cancellationToken);
            await MigrateStringSeriesIdAsync("ingestion_schedules", """
            UPDATE ingestion_schedules target
            SET series_id_numeric = registry.series_id
            FROM canonical_series_registry registry
            WHERE lower(target.source) = registry.provider
              AND lower(target.series_id) = registry.provider_series_id
              AND lower(COALESCE(target.parameters->>'timeframe', target.parameters->>'frequency', '')) = registry.timeframe
            """, cancellationToken);
            await MigrateStringSeriesIdAsync("ingestion_jobs", """
            UPDATE ingestion_jobs target
            SET series_id_numeric = schedule.series_id
            FROM ingestion_schedules schedule
            WHERE target.schedule_id = schedule.id
            """, cancellationToken);
            await MigrateStringSeriesIdAsync("ingestion_executions", """
            UPDATE ingestion_executions target
            SET series_id_numeric = schedule.series_id
            FROM ingestion_schedules schedule
            WHERE target.schedule_id = schedule.id
            """, cancellationToken);
            await MigrateIngestionArtifactsAsync(cancellationToken);
            await MigrateStringSeriesIdAsync("ingestion_artifact_hashes", """
            UPDATE ingestion_artifact_hashes target
            SET series_id_numeric = artifact.series_id
            FROM ingestion_artifacts artifact
            WHERE target.ingestion_id = artifact.id
            """, cancellationToken);
            await MigrateStringSeriesIdAsync("quality_series_group_members", """
            UPDATE quality_series_group_members target
            SET series_id_numeric = series.series_id
            FROM market_data_series series
            WHERE target.dataset_id = series.definition_id
            """, cancellationToken);
            await MigrateStringSeriesIdAsync("quality_validation_target_executions", """
            UPDATE quality_validation_target_executions target
            SET series_id_numeric = series.series_id
            FROM market_data_series series
            WHERE target.dataset_id = series.definition_id
            """, cancellationToken);
            await MigrateStringSeriesIdAsync("quality_findings", """
            UPDATE quality_findings target
            SET series_id_numeric = series.series_id
            FROM market_data_series series
            WHERE target.dataset_id = series.definition_id
            """, cancellationToken);
            await MigrateStringSeriesIdAsync("quality_status_snapshots", """
            UPDATE quality_status_snapshots target
            SET series_id_numeric = series.series_id
            FROM market_data_series series
            WHERE target.dataset_id = series.definition_id
            """, cancellationToken);
            await MigrateSeriesTargetIdsAsync("quality_validation_job_targets", cancellationToken);
            await MigrateSeriesTargetIdsAsync("execution_definition_targets", cancellationToken);
            await ExecutePostgresAsync("""
            CREATE INDEX IF NOT EXISTS ix_ingestion_schedules_enabled_series
                ON ingestion_schedules(enabled, series_id);
            CREATE INDEX IF NOT EXISTS ix_ingestion_jobs_schedule_series_queued
                ON ingestion_jobs(schedule_id, series_id, queued_at DESC);
            CREATE INDEX IF NOT EXISTS ix_ingestion_executions_job_schedule_series_created
                ON ingestion_executions(job_id, schedule_id, series_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS ix_ingestion_artifacts_series_ingested
                ON ingestion_artifacts(series_id, ingested_at DESC);
            CREATE INDEX IF NOT EXISTS ix_quality_findings_active_series
                ON quality_findings(active, dataset_id, series_id, severity, updated_at DESC);
            CREATE INDEX IF NOT EXISTS ix_quality_status_snapshots_series_latest
                ON quality_status_snapshots(dataset_id, series_id, as_of DESC);
            """, cancellationToken);
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(731946120519)", migrationLockConnection);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public async Task EnsureClickHouseSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateClickHouseConnection();
        await connection.OpenAsync(cancellationToken);

        await ExecuteClickHouseAsync(connection, """
            CREATE TABLE IF NOT EXISTS market_ohlcv_bars (
                series_id Int64,
                timestamp DateTime64(3, 'UTC'),
                open Float64,
                high Float64,
                low Float64,
                close Float64,
                volume Float64,
                as_of DateTime64(3, 'UTC'),
                inserted_at DateTime64(3, 'UTC'),
                source_metadata_version String
            )
            ENGINE = MergeTree
            ORDER BY (series_id, timestamp, as_of)
            """, cancellationToken);

        await MigrateClickHouseSeriesIdAsync(connection, cancellationToken);
    }

    private async Task MigrateClickHouseSeriesIdAsync(
        ClickHouseConnection connection,
        CancellationToken cancellationToken)
    {
        await ClickHouseMigrationLock.WaitAsync(cancellationToken);
        NpgsqlConnection? postgresLockConnection = null;
        try
        {
            if (_postgres is not null)
            {
                postgresLockConnection = await _postgres.OpenConnectionAsync(cancellationToken);
                await using var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(731946120518)", postgresLockConnection);
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var type = await GetClickHouseSeriesIdTypeAsync(connection, cancellationToken);
            if (!string.Equals(type, "String", StringComparison.Ordinal))
            {
                return;
            }

            if (_postgres is null)
            {
                throw new InvalidOperationException("PostgreSQL is required to migrate ClickHouse series IDs.");
            }

            var legacyIds = new List<(string DefinitionId, long SeriesId)>();
            await using (var readMappings = Postgres.CreateCommand("""
                SELECT definition_id, series_id
                FROM market_data_series
                """))
            await using (var reader = await readMappings.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    legacyIds.Add((reader.GetString(0), reader.GetInt64(1)));
                }
            }

            if (legacyIds.Count == 0)
            {
                throw new InvalidOperationException(
                    "Cannot migrate ClickHouse series IDs because PostgreSQL contains no canonical series metadata.");
            }

            var mapping = string.Join(
                "\n",
                legacyIds.Select(definition =>
                    $"WHEN '{definition.DefinitionId.Replace("'", "''")}' THEN {definition.SeriesId}"));
            var validIds = string.Join(
                ", ",
                legacyIds.Select(definition => $"'{definition.DefinitionId.Replace("'", "''")}'"));

            await using (var unknown = connection.CreateCommand())
            {
                unknown.CommandText = $"SELECT countIf(series_id NOT IN ({validIds})) FROM market_ohlcv_bars";
                var unknownCount = Convert.ToInt64(await unknown.ExecuteScalarAsync(cancellationToken));
                if (unknownCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot migrate ClickHouse: {unknownCount} rows have series IDs outside the canonical registry.");
                }
            }

            await ExecuteClickHouseAsync(connection, """
                DROP TABLE IF EXISTS market_ohlcv_bars_numeric_migration
                """, cancellationToken);
            await ExecuteClickHouseAsync(connection, """
                CREATE TABLE market_ohlcv_bars_numeric_migration (
                    series_id Int64,
                    timestamp DateTime64(3, 'UTC'),
                    open Float64,
                    high Float64,
                    low Float64,
                    close Float64,
                    volume Float64,
                    as_of DateTime64(3, 'UTC'),
                    inserted_at DateTime64(3, 'UTC'),
                    source_metadata_version String
                )
                ENGINE = MergeTree
                ORDER BY (series_id, timestamp, as_of)
                """, cancellationToken);
            await ExecuteClickHouseAsync(connection, $"""
                INSERT INTO market_ohlcv_bars_numeric_migration
                SELECT
                    CASE series_id
                        {mapping}
                        ELSE 0
                    END,
                    timestamp,
                    open,
                    high,
                    low,
                    close,
                    volume,
                    as_of,
                    inserted_at,
                    source_metadata_version
                FROM market_ohlcv_bars
                """, cancellationToken);
            await ExecuteClickHouseAsync(connection, """
                RENAME TABLE
                    market_ohlcv_bars TO market_ohlcv_bars_legacy,
                    market_ohlcv_bars_numeric_migration TO market_ohlcv_bars
                """, cancellationToken);
        }
        finally
        {
            if (postgresLockConnection is not null)
            {
                await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(731946120518)", postgresLockConnection);
                await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
                await postgresLockConnection.DisposeAsync();
            }

            ClickHouseMigrationLock.Release();
        }
    }

    private static async Task<string?> GetClickHouseSeriesIdTypeAsync(
        ClickHouseConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type
            FROM system.columns
            WHERE database = currentDatabase()
              AND table = 'market_ohlcv_bars'
              AND name = 'series_id'
            LIMIT 1
            """;
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task SeedCanonicalSeriesRegistryAsync(CancellationToken cancellationToken)
    {
        foreach (var definition in TimeLens.Domain.Models.CanonicalSeriesRegistry.All)
        {
            await using var insert = Postgres.CreateCommand("""
                INSERT INTO canonical_series_registry (series_id, provider, provider_series_id, timeframe)
                VALUES (@series_id, @provider, @provider_series_id, @timeframe)
                ON CONFLICT DO NOTHING
                """);
            insert.Parameters.AddWithValue("series_id", definition.SeriesId);
            insert.Parameters.AddWithValue("provider", definition.Provider);
            insert.Parameters.AddWithValue("provider_series_id", definition.ProviderSeriesId);
            insert.Parameters.AddWithValue("timeframe", definition.Timeframe);
            await insert.ExecuteNonQueryAsync(cancellationToken);

            await using var verify = Postgres.CreateCommand("""
                SELECT 1
                FROM canonical_series_registry
                WHERE series_id = @series_id
                  AND provider = @provider
                  AND provider_series_id = @provider_series_id
                  AND timeframe = @timeframe
                """);
            verify.Parameters.AddWithValue("series_id", definition.SeriesId);
            verify.Parameters.AddWithValue("provider", definition.Provider);
            verify.Parameters.AddWithValue("provider_series_id", definition.ProviderSeriesId);
            verify.Parameters.AddWithValue("timeframe", definition.Timeframe);
            if (await verify.ExecuteScalarAsync(cancellationToken) is null)
            {
                throw new InvalidOperationException(
                    $"Canonical series registry entry {definition.SeriesId} conflicts with the persisted registry.");
            }
        }
    }

    private async Task MigrateMarketDataSeriesAsync(CancellationToken cancellationToken)
    {
        if (await GetSeriesIdColumnTypeAsync("market_data_series", cancellationToken) != "text")
        {
            return;
        }

        await ExecutePostgresAsync("""
            ALTER TABLE market_data_series ADD COLUMN IF NOT EXISTS definition_id text;
            ALTER TABLE market_data_series ADD COLUMN IF NOT EXISTS provider_series_id text;
            ALTER TABLE market_data_series ADD COLUMN IF NOT EXISTS series_id_numeric bigint;
            UPDATE market_data_series
            SET definition_id = COALESCE(definition_id, id),
                provider_series_id = COALESCE(provider_series_id, series_id);
            UPDATE market_data_series target
            SET series_id_numeric = registry.series_id
            FROM canonical_series_registry registry
            WHERE lower(target.provider) = registry.provider
              AND lower(target.series_id) = registry.provider_series_id
              AND lower(target.timeframe) = registry.timeframe;
            """, cancellationToken);
        await EnsureNoUnmappedSeriesIdsAsync("market_data_series", cancellationToken);
        await ExecutePostgresAsync("""
            ALTER TABLE market_data_series DROP CONSTRAINT IF EXISTS market_data_series_pkey;
            ALTER TABLE market_data_series DROP COLUMN IF EXISTS id;
            ALTER TABLE market_data_series DROP COLUMN series_id;
            ALTER TABLE market_data_series RENAME COLUMN series_id_numeric TO series_id;
            ALTER TABLE market_data_series ALTER COLUMN definition_id SET NOT NULL;
            ALTER TABLE market_data_series ALTER COLUMN provider_series_id SET NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_market_data_series_series_id ON market_data_series(series_id);
            """, cancellationToken);
    }

    private async Task MigrateIngestionArtifactsAsync(CancellationToken cancellationToken)
    {
        if (await GetSeriesIdColumnTypeAsync("ingestion_artifacts", cancellationToken) != "text")
        {
            return;
        }

        await ExecutePostgresAsync("""
            ALTER TABLE ingestion_artifacts RENAME COLUMN dataset_id TO definition_id;
            ALTER TABLE ingestion_artifacts ALTER COLUMN definition_id DROP NOT NULL;
            ALTER TABLE ingestion_artifacts ADD COLUMN IF NOT EXISTS provider_series_id text;
            ALTER TABLE ingestion_artifacts ADD COLUMN IF NOT EXISTS series_id_numeric bigint;
            UPDATE ingestion_artifacts target
            SET series_id_numeric = series.series_id
            FROM market_data_series series
            WHERE target.definition_id = series.definition_id;
            UPDATE ingestion_artifacts target
            SET provider_series_id = registry.provider_series_id
            FROM canonical_series_registry registry
            WHERE target.series_id_numeric = registry.series_id;
            """, cancellationToken);
        await EnsureNoUnmappedSeriesIdsAsync("ingestion_artifacts", cancellationToken);
        await ExecutePostgresAsync("""
            ALTER TABLE ingestion_artifacts DROP COLUMN series_id;
            ALTER TABLE ingestion_artifacts RENAME COLUMN series_id_numeric TO series_id;
            ALTER TABLE ingestion_artifacts ALTER COLUMN provider_series_id SET NOT NULL;
            """, cancellationToken);
    }

    private async Task MigrateSeriesTargetIdsAsync(string tableName, CancellationToken cancellationToken)
    {
        await ExecutePostgresAsync($"""
            UPDATE {tableName} target
            SET target_id = series.series_id::text
            FROM market_data_series series
            WHERE lower(target.target_type) IN ('series', 'dataset')
              AND target.target_id = series.definition_id;
            UPDATE {tableName} target
            SET target_id = registry.series_id::text
            FROM canonical_series_registry registry
            WHERE lower(target.target_type) IN ('series', 'dataset')
              AND lower(target.target_id) = registry.provider_series_id
              AND (
                    (SELECT count(*) FROM canonical_series_registry duplicate
                     WHERE duplicate.provider = registry.provider
                       AND duplicate.provider_series_id = registry.provider_series_id) = 1
                    OR registry.timeframe = '1h'
                  );
            """, cancellationToken);
    }

    private async Task MigrateStringSeriesIdAsync(
        string tableName,
        string populateSql,
        CancellationToken cancellationToken)
    {
        if (await GetSeriesIdColumnTypeAsync(tableName, cancellationToken) != "text")
        {
            return;
        }

        await ExecutePostgresAsync($"ALTER TABLE {tableName} ADD COLUMN IF NOT EXISTS series_id_numeric bigint", cancellationToken);
        await ExecutePostgresAsync(populateSql, cancellationToken);
        await EnsureNoUnmappedSeriesIdsAsync(tableName, cancellationToken);
        if (tableName == "ingestion_artifact_hashes")
        {
            await ExecutePostgresAsync(
                "ALTER TABLE ingestion_artifact_hashes DROP CONSTRAINT IF EXISTS ingestion_artifact_hashes_pkey",
                cancellationToken);
        }

        await ExecutePostgresAsync($"""
            ALTER TABLE {tableName} DROP COLUMN series_id;
            ALTER TABLE {tableName} RENAME COLUMN series_id_numeric TO series_id
            """, cancellationToken);
        if (tableName == "ingestion_artifact_hashes")
        {
            await ExecutePostgresAsync(
                "ALTER TABLE ingestion_artifact_hashes ADD PRIMARY KEY (series_id, parsed_sha256)",
                cancellationToken);
        }
    }

    private async Task<string?> GetSeriesIdColumnTypeAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var command = Postgres.CreateCommand("""
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = current_schema()
              AND table_name = @table_name
              AND column_name = 'series_id'
            """);
        command.Parameters.AddWithValue("table_name", tableName);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task EnsureNoUnmappedSeriesIdsAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var command = Postgres.CreateCommand($"SELECT count(*) FROM {tableName} WHERE series_id_numeric IS NULL");
        var unmappedCount = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
        if (unmappedCount != 0)
        {
            throw new InvalidOperationException(
                $"Cannot migrate {tableName}: {unmappedCount} series IDs do not match the canonical registry.");
        }
    }

    private async Task ExecutePostgresAsync(string sql, CancellationToken cancellationToken)
    {
        await using var command = Postgres.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteClickHouseAsync(ClickHouseConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
