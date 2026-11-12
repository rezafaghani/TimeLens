using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orleans;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Observability;
using TimeLens.Ingestion.Services;

namespace TimeLens.Ingestion.Grains;

public class EnergyChartsPriceGrain(
    EnergyChartsPriceClient energyChartsClient,
    CoinMetricsClient coinMetricsClient,
    EnergyChartsPriceNormalizer energyChartsNormalizer,
    CoinMetricsNormalizer coinMetricsNormalizer,
    IngestionArtifactWriter artifactWriter,
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    ILogger<EnergyChartsPriceGrain> logger) : Grain, IEnergyChartsPriceGrain
{
    public async Task IngestAsync(string messageJson, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var message = JsonSerializer.Deserialize<IngestionJobMessage>(messageJson)
            ?? throw new InvalidOperationException("Ingestion job message payload is invalid.");
        using var activity = TimeLensTelemetry.ActivitySource.StartActivity("IngestionExecution");
        activity?.SetTag("timelens.job.id", message.JobId);
        activity?.SetTag("timelens.execution.id", message.ExecutionId);
        activity?.SetTag("market.provider", message.Source);
        activity?.SetTag("market.series_id", message.SeriesId);

        using var runningScope = scopeFactory.CreateScope();
        await runningScope.ServiceProvider.GetRequiredService<IIngestionControlRepository>()
            .MarkJobRunningAsync(message.JobId, message.ExecutionId, cancellationToken);

        try
        {
            var isCoinMetrics = message.Source == "coin-metrics-community";
            var symbol = isCoinMetrics ? Get(message.Parameters, "asset", "btc") : Get(message.Parameters, "bzn", "DK1");
            var timeframe = isCoinMetrics ? Get(message.Parameters, "frequency", "1d") : Get(message.Parameters, "timeframe", "1h");
            var start = Resolve(message.WindowStartExpression, -Math.Max(message.LookbackHours > 0 ? message.LookbackHours : options.Value.LookbackHours, 1));
            var end = Resolve(message.WindowEndExpression, 24);
            if (start >= end)
            {
                throw new ArgumentException("A valid half-open ingestion window is required.");
            }

            var definition = new MarketDataRequestDefinition
            {
                Endpoint = message.Endpoint,
                Parameters = new Dictionary<string, string>(message.Parameters)
            };

            using var providerActivity = TimeLensTelemetry.ActivitySource.StartActivity("ProviderRequest");
            var providerStarted = Stopwatch.GetTimestamp();
            providerActivity?.SetTag("market.provider", message.Source);
            providerActivity?.SetTag("market.symbol", symbol);
            providerActivity?.SetTag("market.timeframe", timeframe);
            var response = isCoinMetrics
                ? await coinMetricsClient.GetAsync(symbol, Get(message.Parameters, "metric"), timeframe, start, end, cancellationToken)
                : await energyChartsClient.GetAsync(symbol, start, end, cancellationToken);
            TimeLensTelemetry.ProviderRequestDuration.Record(Stopwatch.GetElapsedTime(providerStarted).TotalSeconds, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            var rawArtifact = await artifactWriter.SaveRawAsync(message, response.RawJson, 1, cancellationToken);
            using var document = response.Parse();

            NormalizedDataset dataset;
            using (TimeLensTelemetry.ActivitySource.StartActivity("NormalizeMarketData"))
            {
                dataset = isCoinMetrics
                    ? coinMetricsNormalizer.Normalize(definition, document.RootElement)
                    : energyChartsNormalizer.Normalize(definition, document.RootElement);
            }
            dataset.Metadata.SeriesId = message.SeriesId;

            using var metadataScope = scopeFactory.CreateScope();
            var saved = await metadataScope.ServiceProvider.GetRequiredService<IDatasetRepository>().UpsertAsync(dataset.Metadata, cancellationToken);
            dataset.Batch.SourceMetadataVersion = saved.LastIngestedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            await artifactWriter.SaveParsedAndPublishAsync(
                rawArtifact,
                saved.SeriesId,
                dataset.Batch.SourceMetadataVersion,
                dataset.Batch.Points,
                cancellationToken);

            using var completedScope = scopeFactory.CreateScope();
            await completedScope.ServiceProvider.GetRequiredService<IIngestionControlRepository>()
                .MarkJobArtifactsQueuedAsync(message.JobId, message.ExecutionId, 1, cancellationToken);
            TimeLensTelemetry.IngestionExecutions.Add(1, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            TimeLensTelemetry.IngestionDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            logger.LogInformation("Archived and queued {PointCount} points for {Provider} {SeriesId} {Timeframe}.", dataset.Batch.Points.Count, saved.Provider, saved.SeriesId, saved.Timeframe);
        }
        catch (Exception ex)
        {
            activity.RecordException(ex);
            TimeLensTelemetry.IngestionFailures.Add(1, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            using var failedScope = scopeFactory.CreateScope();
            await failedScope.ServiceProvider.GetRequiredService<IIngestionControlRepository>()
                .MarkJobFailedAsync(message.JobId, message.ExecutionId, ex.Message, CancellationToken.None);
            throw;
        }
    }

    private static string Get(Dictionary<string, string> parameters, string key, string fallback = "") =>
        parameters.TryGetValue(key, out var value) ? value : fallback;

    private static DateTimeOffset Resolve(string expression, int fallbackHours)
    {
        if (!string.IsNullOrWhiteSpace(expression) && DateTimeExpression.TryResolve(expression, out var resolved, "UTC"))
        {
            return resolved;
        }

        return fallbackHours == 0 ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddHours(fallbackHours);
    }
}
