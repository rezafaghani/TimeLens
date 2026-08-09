# TimeLens

TimeLens is a provider-agnostic time-series and market-data intelligence platform.

The first supported market-data domain is cryptocurrency OHLCV bars from Coinbase Exchange. The core architecture stays generic so equities, ETFs, indexes, forex, and other providers can be added later without redesigning ingestion, validation, storage, or visualization.

## What It Does

- Browse market instruments such as `BTC-USD` and `ETH-USD`.
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

Use `podman compose` instead of `docker compose` if that is your local runtime.

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

- Current seeded instruments are `BTC-USD` and `ETH-USD`.
- Current market-data type is OHLCV bars.
- Provider credentials must come from environment variables or secret configuration. Do not commit API keys.
