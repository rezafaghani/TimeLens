namespace TimeLens.Infrastructure;

using ClickHouse.Client.ADO;
using Npgsql;

public sealed class TimeLensContext
{
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
                series_id text NOT NULL,
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

            CREATE INDEX IF NOT EXISTS ix_ingestion_schedules_enabled_series
                ON ingestion_schedules(enabled, series_id);

            CREATE TABLE IF NOT EXISTS ingestion_jobs (
                id text PRIMARY KEY,
                schedule_id text NOT NULL,
                series_id text NOT NULL,
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
                series_id text NOT NULL,
                status text NOT NULL,
                created_at timestamptz NOT NULL,
                started_at timestamptz NULL,
                finished_at timestamptz NULL,
                inserted integer NOT NULL,
                skipped integer NOT NULL,
                error text NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_ingestion_executions_job_schedule_series_created
                ON ingestion_executions(job_id, schedule_id, series_id, created_at DESC);

            CREATE TABLE IF NOT EXISTS market_data_series (
                id text PRIMARY KEY,
                series_id text NOT NULL,
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
                series_id text NOT NULL,
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
                series_id text NOT NULL,
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
                series_id text NOT NULL,
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
                series_id text NOT NULL,
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
    }

    public async Task EnsureClickHouseSchemaAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CreateClickHouseConnection();
        await connection.OpenAsync(cancellationToken);

        await ExecuteClickHouseAsync(connection, """
            CREATE TABLE IF NOT EXISTS market_ohlcv_bars (
                series_id String,
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
    }

    private static async Task ExecuteClickHouseAsync(ClickHouseConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
