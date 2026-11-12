# Ingestion, Artifacts, and Series IDs

## Ingestion flow

1. The scheduler places ingestion jobs on RabbitMQ (`timelens.ingestion.jobs`).
2. The ingestion worker fetches provider responses and stores the original JSON in SeaweedFS before parsing it.
3. It normalizes the response, stores parsed time-series JSON, and publishes an artifact message to `timelens.ingestion.artifacts`.
4. The message carries a UUIDv7 ingestion ID, canonical numeric `SeriesId`, provider-series metadata, ingestion time, object keys, parsed-data SHA-256, and counts.
5. The Inserter consumes the message, records the artifact in PostgreSQL, verifies the parsed object and its hash, then writes new points to ClickHouse.

Artifact keys are date-partitioned and include the canonical series ID and UUIDv7, for example:

```text
raw/5/2026/11/12/{ingestion-uuid}.json
parsed/5/2026/11/12/{ingestion-uuid}.json
```

PostgreSQL tracks the raw and parsed keys, parsed hash, processing status, counts, retries, and duplicate relationship. A repeated parsed hash for the same `SeriesId` is marked duplicate and is not inserted into ClickHouse. A failed message is retried up to the configured `ArtifactWorker__MaxRetries` limit; exhausted failures remain recorded in PostgreSQL.

The Inserter is a background worker, not an HTTP or gRPC service. The ingestion worker communicates with it through RabbitMQ and SeaweedFS only.

## Storage configuration

Docker Compose starts SeaweedFS and persists its data in the `seaweedfs-data` volume. The services use the S3-compatible endpoint `http://seaweedfs:8333` and bucket `timelens-ingestion`. Configure credentials with:

- `SEAWEEDFS_ACCESS_KEY`
- `SEAWEEDFS_SECRET_KEY`

The Compose file has local-development defaults; use secret configuration and unique credentials outside local development. The client options also support `ObjectStorage__ServiceUrl`, `ObjectStorage__BucketName`, `ObjectStorage__AccessKey`, `ObjectStorage__SecretKey`, and `ObjectStorage__Region`.

`deploy/k8s/seaweedfs.yaml` deploys only the storage service. Before applying it, create namespace `timelens` and a Secret named `seaweedfs-credentials` in that namespace with keys `access-key` and `secret-key`. Application deployments must separately configure their object-storage endpoint, bucket, and credentials.

## Canonical numeric series IDs

`SeriesId` is a `long` in application and API contracts and is the canonical identity in PostgreSQL, ClickHouse, RabbitMQ messages, and the client. Provider series IDs remain descriptive metadata and are not interchangeable with `SeriesId`.

Assignments live in `CanonicalSeriesRegistry` in the domain project. The registry is shared by all environments through source control. IDs are append-only: never change or reuse an existing ID. An unregistered definition cannot be used to create a schedule or write metadata.

| ID | Series |
| ---: | --- |
| 1 | Energy-Charts DK1 day-ahead price, 1h |
| 2 | Energy-Charts DK1 day-ahead price, 15m |
| 3 | Energy-Charts DK2 day-ahead price, 1h |
| 4 | Energy-Charts DK2 day-ahead price, 15m |
| 5 | Coinbase BTC-USD, 1m |
| 6 | Coinbase BTC-USD, 5m |
| 7 | Coinbase BTC-USD, 15m |
| 8 | Coinbase BTC-USD, 1h |
| 9 | Coinbase BTC-USD, 1d |
| 10 | Coinbase ETH-USD, 1m |
| 11 | Coinbase ETH-USD, 5m |
| 12 | Coinbase ETH-USD, 15m |
| 13 | Coinbase ETH-USD, 1h |
| 14 | Coinbase ETH-USD, 1d |
| 15 | Coin Metrics BTC HashRate, 1d |
| 16 | Coin Metrics BTC AdrActCnt, 1d |
| 17 | Coin Metrics BTC TxCnt, 1d |

Use the numeric ID in dataset, ingestion, quality, and analytics API requests. For example, `GET /api/datasets?seriesId=5` searches for Coinbase BTC-USD at 1m.

## Existing database migration

On startup, the API and Inserter ensure the PostgreSQL and ClickHouse schemas and migrate legacy tables from `public`. PostgreSQL tables are grouped under `market_data`, `ingestion`, `execution`, and `quality`; the unused historical `quality_validator_executions` table is moved to `legacy` instead of being deleted. Legacy string series IDs are migrated to canonical numeric IDs, with old metadata identifiers retained as `definition_id`.

The ClickHouse connection selects the `timelens` database (configurable with `CLICKHOUSE_DB`). `market_ohlcv_bars` uses `MergeTree` with sort key `(series_id, timestamp, as_of)`, matching the application's series-and-time-range reads while preserving version history. Reads select the latest eligible version per timestamp with `LIMIT 1 BY`, avoiding a full window-function rank.

Old ClickHouse rows are copied to the numeric-ID table, and the previous table is retained as `market_ohlcv_bars_legacy`. Back up PostgreSQL and ClickHouse before deploying these migrations to an existing environment. Migration stops with an error if existing identifiers cannot be mapped to the canonical registry; resolve the unmapped data rather than assigning IDs differently between environments.
