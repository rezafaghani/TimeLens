# Market Data Validation

Validation remains a first-class TimeLens subsystem after the market-data migration. It runs through the existing execution platform, RabbitMQ scheduling, Orleans validation grains, and plugin discovery. Validation plugins receive normalized application data through `ExecutionPluginContext`; they do not query ClickHouse or external providers directly.

## Calendars

Missing-interval and availability validation use a market calendar abstraction. The current implementation supports `crypto-24x7`, which expects continuous observations at the configured timeframe. Unknown calendars currently fall back to `crypto-24x7`.

Future equity calendars should add implementations for exchange sessions such as NYSE or NASDAQ, including weekends, holidays, early closes, and special sessions. Missing data outside expected sessions should be reported as `market_closed` or skipped, not as a gap.

## Generic Validators

The existing generic validators remain usable for market data:

- `metadata.required`
- `availability.empty-data`
- `completeness.missing-timestamps`
- `timestamps.alignment`
- `freshness.latest-point`
- `duplicates.timestamp-conflict`
- `validity.value-range`
- `continuity.rate-of-change`
- `stale.flat-line`

Freshness uses provider/instrument/timeframe metadata and supports `allowedDelay` or `gracePeriod`. If no explicit `allowedDelay` is provided, the default is `timeframe + gracePeriod`.

## OHLCV Validators

Market-specific plugins are separate and discoverable:

- `validity.ohlc-consistency`: checks positive OHLC prices and candle constraints such as `high >= low`, `high >= open/close`, and `low <= open/close`.
- `validity.volume`: checks volume constraints. Zero volume is allowed by default; set `allowZeroVolume` to `false` where a provider/instrument policy requires positive volume.

Findings include sample timestamps and JSON details with field, observed value, and expected constraint.

## Statistical Validators

Initial deterministic statistical checks are implemented without ML:

- `anomaly.price-spike`: rolling close-price z-score and percentage-change checks.
- `anomaly.volume-spike`: rolling volume z-score and percentage-change checks.
- `stale.flat-price`: repeated close prices over a configured observation count.
- `anomaly.abnormal-volatility`: rolling z-score over absolute returns.

Common parameters include `rollingWindow`, `minimumObservations`, `zScoreThreshold`, `percentageChangeThreshold`, `minimumConsecutiveBars`, and `tolerance`. If there are too few observations, statistical validators emit `insufficient_data` instead of pretending the check passed.

## Scheduling And Backfill

Scheduled validation continues to use the existing quality validation jobs and validation worker. Manual and historical/backfill runs use the same job/run APIs and create normal execution records, so validation history stays intact.

Disabled validation jobs are not queued by the scheduler. Manual runs still require an explicit run request.

## Plugin Development

Add a new validation plugin by implementing `IExecutionPlugin`, normally by deriving from `QualityValidationPlugin`. The API and validation worker discover plugin types from the validation assembly and register metadata automatically. Do not add central switch statements, and do not query ClickHouse from the plugin.
