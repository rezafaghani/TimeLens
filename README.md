# TimeLens

TimeLens is a provider-agnostic time-series and market-data intelligence platform.

The first supported market-data domains are cryptocurrency OHLCV bars from Coinbase Exchange and European day-ahead electricity prices from Energy-Charts. The core architecture stays generic so equities, ETFs, indexes, forex, and other providers can be added later without redesigning ingestion, validation, storage, or visualization.

## What It Does

- Browse market instruments such as `BTC-USD` and `ETH-USD`.
- Browse energy-market series such as Danish `DK1` and `DK2` day-ahead electricity prices.
- Inspect OHLCV bars in charts and tables.
- Track ingestion schedules, queued jobs, executions, inserted rows, skipped rows, and failures.
- Queue manual historical backloads.
- Validate market data quality and record validation findings.
- Store metadata in PostgreSQL and versioned market bars in ClickHouse.
- Use RabbitMQ workers for ingestion and validation execution.

## Architecture

```text
External Provider
  -> Provider Adapter
  -> Ingestion Worker
  -> Insert API
  -> ClickHouse time-series storage
  -> Validation
  -> API/UI

Planned:
Validated Market Data -> Analytics -> Strategy/Signals -> Backtesting
```

TimeLens is not a cryptocurrency trading bot and does not place trades.

## Stack

- Backend: .NET 10
- UI: React 19 + Vite
- Data: PostgreSQL 17 + ClickHouse 25.3
- Messaging: RabbitMQ 4 Management
- Tests: xUnit and Vitest
- Containers: Dockerfiles plus `docker-compose.yml`

## Services

| Service | Purpose | Port |
| --- | --- | --- |
| `ui` | React/Vite app served by Nginx | `8080` |
| `api` | Main HTTP API, metadata reads, OHLCV reads, ingestion control, validation | `5100` |
| `insert` | Insert API plus gRPC ingestion endpoint | `5101`, `5103` |
| `scheduler` | Queues ingestion and validation jobs on schedule | internal |
| `ingestion` | Consumes RabbitMQ jobs and loads provider data | internal |
| `validation-worker` | Consumes validation jobs | `5104` |
| `postgres` | Metadata, schedules, jobs, executions, validation history | `5432` |
| `clickhouse` | Versioned OHLCV bars | `8123`, `9000` |
| `rabbitmq` | RabbitMQ broker and management UI | `5672`, `15672` |
| `skywalking-ui` | SkyWalking UI for local observability | `8081` |
| `otel-collector` | Receives TimeLens OTLP telemetry and forwards it to SkyWalking OAP | `4317`, `4318` |

## Useful APIs

```text
GET /api/datasets?search=BTC
GET /api/datasets/{seriesId}/series?start=now-24h&end=now&timeZone=UTC
GET /api/ingestion/series/{seriesId}/schedules
GET /api/ingestion/series/{seriesId}/jobs
GET /api/ingestion/series/{seriesId}/executions
GET /api/data-quality/findings?datasetId={seriesId}
```

Date expressions support `now`, `today`, `today+N`, `today-N`, `now+Nh`, `now-Nh`, and ISO timestamps.

## Run With Compose

```bash
docker compose build
docker compose up -d
```

Then open:

- UI: `http://localhost:8080`
- RabbitMQ management: `http://localhost:15672`
- SkyWalking UI: `http://localhost:8081`
- SkyWalking OTLP traces: `http://localhost:8081/zipkin`

Use `podman compose` instead of `docker compose` if that is your local runtime.

## Observability

TimeLens services export OpenTelemetry traces and metrics to the local OpenTelemetry Collector. The Collector forwards them to SkyWalking OAP. Runtime logs stay on the normal console path by default; set `OTEL_EXPORT_LOGS=true` if you explicitly want to test OTLP log export.

```text
TimeLens Services
  -> OTLP
  -> OpenTelemetry Collector
  -> SkyWalking OAP
  -> SkyWalking UI
```

SkyWalking 10.4 stores OTLP traces through its Zipkin-compatible trace path. If the APM service list is populated but trace search looks empty, open `http://localhost:8081/zipkin` and search for services such as `timelens-api`, `timelens-ingestion`, or `timelens-insert`.

### SkyWalking Dashboard

Local compose enables SkyWalking dashboard editing with `SKYWALKING_ENABLE_DASHBOARD_EDIT=true`.

To create a TimeLens dashboard:

1. Open `http://localhost:8081`.
2. Go to `Dashboards`.
3. Choose `New Dashboard`.
4. Use layer `GENERAL` and service widgets for `timelens-api`, `timelens-ingestion`, `timelens-insert`, `timelens-scheduler`, and `timelens-validation-worker`.
5. Use `http://localhost:8081/zipkin` for OTLP trace drill-down.

Keep this enabled for local development only. For shared or production-like deployments, set `SKYWALKING_ENABLE_DASHBOARD_EDIT=false`.

## Local Development

```bash
dotnet restore TimeLens.sln
dotnet build TimeLens.sln
dotnet run --project src/TimeLens.API/TimeLens.API.csproj
```

```bash
cd src/TimeLens.Client
npm install
npm start
```

Set `VITE_API_BASE_URL` if the API is not available through `/api`.

## Tests

```bash
dotnet test TimeLens.sln
cd src/TimeLens.Client && npm test
```

## Planned Work

- Analytics: returns, moving averages, volatility, RSI/MACD, volume indicators.
- Derived time series that can be stored, displayed, and validated.
- Strategy/signals that consume APIs/application abstractions.
- Backtesting with point-in-time data only, including positions, trades, PnL, drawdown, costs, and slippage.

## Notes

- Current seeded crypto instruments are `BTC-USD` and `ETH-USD`.
- Current seeded energy series are Energy-Charts `DK1` and `DK2` day-ahead prices in `EUR / MWh`.
- Current market-data types are OHLCV bars and electricity prices.
- Provider credentials must come from environment variables or secret configuration. Do not commit API keys.
