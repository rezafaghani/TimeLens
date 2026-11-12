# TimeLens

TimeLens is a provider-agnostic time-series and market-data intelligence platform. Its current sources are cryptocurrency OHLCV bars from Coinbase Exchange, Bitcoin network metrics from Coin Metrics Community, and European day-ahead electricity prices from Energy-Charts.

## What It Does

- Browse crypto, energy, and network-metric series and inspect stored data.
- Compare series and request cross-market correlations.
- Track ingestion schedules, jobs, executions, inserted rows, skips, failures, and duplicates.
- Queue manual historical backfills and validate market-data quality.
- Store metadata and ingestion state in PostgreSQL and versioned time-series data in ClickHouse.
- Store raw and parsed ingestion artifacts in SeaweedFS using its S3-compatible API.

Every series has a globally stable numeric `SeriesId`. IDs are assigned in the checked-in, append-only registry; provider identifiers and other metadata remain separate. See [Ingestion and series IDs](docs/ingestion.md) before adding or migrating series.

## Architecture

```text
Scheduler -> RabbitMQ ingestion jobs -> Ingestion worker -> Provider
                                                  |
                                  Raw + parsed JSON artifacts
                                                  v
                                              SeaweedFS
                                                  |
                                 RabbitMQ artifact messages
                                                  v
                 Inserter -> PostgreSQL tracking/dedup -> ClickHouse
                                                  |
                                      Validation -> API/UI
```

The Inserter is a background RabbitMQ consumer; it does not expose an ingestion HTTP or gRPC API. The artifact message identifies the stored objects and their canonical numeric series. The Inserter verifies the parsed artifact, records its outcome, skips duplicate parsed data for the same series, and inserts new data into ClickHouse.

TimeLens is not a cryptocurrency trading bot and does not place trades.

## Stack

- Backend: .NET 11
- UI: React 19 + Vite
- Data: PostgreSQL 17 + ClickHouse 25.3
- Messaging: RabbitMQ 4 Management
- Artifact storage: SeaweedFS S3-compatible API
- Tests: xUnit and Vitest
- Local stack: Docker or Podman Compose

## Services

| Service | Purpose | Host ports |
| --- | --- | --- |
| `ui` | React/Vite app served by Nginx | `8080` |
| `api` | HTTP API for datasets, time series, ingestion control, analytics, and validation | `5100` |
| `insert` | RabbitMQ artifact consumer and ClickHouse writer; no public API | internal |
| `scheduler` | Queues ingestion and validation jobs | internal |
| `ingestion` | Fetches provider data and writes artifacts | internal |
| `validation-worker` | Consumes validation jobs | `5104` |
| `postgres` | Series metadata, schedules, jobs, artifacts, and validation history | `5432` |
| `clickhouse` | Versioned time-series data | `8123`, `9000` |
| `rabbitmq` | Message broker and management UI | `5672`, `15672` |
| `seaweedfs` | S3-compatible raw and parsed artifact storage | `8333`, `8888`, `9333` |
| `lgtm` | Grafana, Tempo, Loki, Prometheus, and OTLP collector | `3000`, `4317`, `4318` |

## Useful APIs

```text
GET /api/datasets?search=BTC
GET /api/datasets?seriesId=5
GET /api/datasets/{seriesId}/metadata
GET /api/datasets/{seriesId}/series?start=now-24h&end=now&timeZone=UTC
GET /api/ingestion/series/{seriesId}/schedules
GET /api/ingestion/series/{seriesId}/jobs
GET /api/ingestion/series/{seriesId}/executions
GET /api/data-quality/findings?seriesId=5
GET /api/analytics/correlation?leftSeriesId=5&rightSeriesId=1&start=now-48h&end=now&bucket=1h&maxLag=24
```

Date expressions include `now`, `today`, `today+N`, `today-N`, `now+Nh`, `now-Nh`, and ISO timestamps. Use `bucket=1d` when comparing daily network metrics with energy prices.

## Run With Compose

Copy the example environment file and replace the development passwords before starting the stack:

```bash
cp .env.example .env
# Edit .env and change the placeholder passwords.
docker compose build
docker compose up -d
```

Use `podman compose` instead of `docker compose` if Podman is your local runtime. Open:

- UI: `http://localhost:8080`
- RabbitMQ management: `http://localhost:15672`
- SeaweedFS S3 endpoint: `http://localhost:8333`
- Grafana: `http://localhost:3000`

The sample credentials in `.env.example` are for local development only. Do not use them in a shared or production environment.

The Kubernetes manifest at `deploy/k8s/seaweedfs.yaml` provisions SeaweedFS only. It expects a `seaweedfs-credentials` Secret in namespace `timelens` with `access-key` and `secret-key` keys; it is not a complete deployment of the TimeLens application.

## Local Development

The solution targets .NET 11. For the API to run locally, configure PostgreSQL, ClickHouse, and RabbitMQ using the `ConnectionStrings__*` and `RabbitMq__*` environment variables. Run the full stack with Compose for a ready-to-use local setup.

```bash
dotnet restore TimeLens.slnx
dotnet build TimeLens.slnx
dotnet test TimeLens.slnx
```

```bash
cd src/TimeLens.Client
npm install
npm start
npm test
npm run build
```

Set `VITE_API_BASE_URL` if the API is not available through the client’s `/api` route.

## Documentation

- [Ingestion, artifacts, and canonical series IDs](docs/ingestion.md)
- [Observability](docs/observability.md)
- [Market data validation](docs/validation-platform-v2.md)

## Planned Work

- Additional providers and market-data domains.
- Derived time series and further analytics indicators.
- Strategy and signal APIs, followed by point-in-time backtesting.

## Notes

- Seeded crypto instruments are `BTC-USD` and `ETH-USD`; seeded Coin Metrics series are Bitcoin `HashRate`, `AdrActCnt`, and `TxCnt`.
- Seeded energy series are Energy-Charts `DK1` and `DK2` day-ahead prices in `EUR / MWh`.
- Provider credentials and non-development passwords must come from environment variables or secret configuration. Never commit secrets.
