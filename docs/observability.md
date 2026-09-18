# Observability

TimeLens sends OpenTelemetry traces, metrics, and logs directly to the local Grafana LGTM container. LGTM bundles Grafana, Tempo, Loki, Prometheus, and an OpenTelemetry Collector for development and testing.

## Start

```bash
docker compose up -d
```

Open `http://localhost:3000`. The provisioned **TimeLens Overview** dashboard opens as the home page and refreshes every 10 seconds. No Grafana setup or login is required.

The dashboard includes:

- request rate, error rate, P95 latency, and working-set memory;
- operations and dependencies by service;
- application logs from Loki;
- recent distributed traces from Tempo.

Use the **Service** selector to filter the dashboard. Click a log or trace row for its details. For ad-hoc queries, open **Explore** and select Prometheus, Loki, or Tempo.

## Configuration

Compose sends every TimeLens service to `http://lgtm:4317`. The relevant environment variables are:

| Variable | Default | Purpose |
| --- | --- | --- |
| `OTEL_ENABLED` | `true` | Enable trace and metric export |
| `OTEL_EXPORT_LOGS` | `true` | Enable log export |
| `OTEL_TRACES_SAMPLING_RATIO` | `1.0` | Trace sampling ratio from `0` to `1` |
| `DEPLOYMENT_ENVIRONMENT` | `local` | Resource environment label |
| `GRAFANA_OTEL_LGTM_VERSION` | `0.30.1` | LGTM container version |

Grafana data is stored in the `lgtm-data` Compose volume. The dashboard source is `observability/grafana/timelens-overview.json` and is loaded automatically through Grafana file provisioning.

## Troubleshooting

```bash
docker compose ps lgtm
docker compose logs lgtm
```

If a panel is empty, select **All** services and widen the time range. Generate API or ingestion activity, then allow one export interval for data to appear.
