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
    EnergyChartsPriceNormalizer normalizer,
    IngestionWriteClient insertClient,
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

        var inserted = 0;
        var skipped = 0;

        try
        {
            var biddingZone = Get(message.Parameters, "bzn", "DK1");
            var timeframe = Get(message.Parameters, "timeframe", "1h");
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
                {
                    ["bzn"] = biddingZone,
                    ["timeframe"] = timeframe
                }
            };

            using var providerActivity = TimeLensTelemetry.ActivitySource.StartActivity("ProviderRequest");
            var providerStarted = Stopwatch.GetTimestamp();
            providerActivity?.SetTag("market.provider", message.Source);
            providerActivity?.SetTag("market.symbol", biddingZone);
            providerActivity?.SetTag("market.timeframe", timeframe);
            using var document = await energyChartsClient.GetAsync(biddingZone, start, end, cancellationToken);
            TimeLensTelemetry.ProviderRequestDuration.Record(Stopwatch.GetElapsedTime(providerStarted).TotalSeconds, KeyValuePair.Create<string, object?>("market.provider", message.Source));

            NormalizedDataset dataset;
            using (TimeLensTelemetry.ActivitySource.StartActivity("NormalizeMarketData"))
            {
                dataset = normalizer.Normalize(definition, document.RootElement);
            }
            dataset.Metadata.SeriesId = message.SeriesId;

            using var metadataScope = scopeFactory.CreateScope();
            var saved = await metadataScope.ServiceProvider.GetRequiredService<IDatasetRepository>().UpsertAsync(dataset.Metadata, cancellationToken);
            dataset.Batch.DatasetId = saved.Id;
            dataset.Batch.SourceMetadataVersion = saved.LastIngestedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

            foreach (var chunk in dataset.Batch.Points.Chunk(Math.Max(message.BatchSize > 0 ? message.BatchSize : options.Value.BatchSize, 1)))
            {
                using var persistActivity = TimeLensTelemetry.ActivitySource.StartActivity("PersistMarketData");
                var result = await insertClient.InsertBatchAsync(new TimeSeriesBatchRequest
                {
                    DatasetId = dataset.Batch.DatasetId,
                    SourceMetadataVersion = dataset.Batch.SourceMetadataVersion,
                    Points = chunk.ToList()
                }, cancellationToken);
                inserted += result.Inserted;
                skipped += result.Skipped;
            }

            using var completedScope = scopeFactory.CreateScope();
            await completedScope.ServiceProvider.GetRequiredService<IIngestionControlRepository>()
                .MarkJobCompletedAsync(message.JobId, message.ExecutionId, inserted, skipped, cancellationToken);
            TimeLensTelemetry.IngestionExecutions.Add(1, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            TimeLensTelemetry.RecordsIngested.Add(inserted, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            TimeLensTelemetry.IngestionDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            logger.LogInformation("Inserted {Inserted}, skipped {Skipped} for {Provider} {SeriesId} {Timeframe}.", inserted, skipped, saved.Provider, saved.SeriesId, saved.Timeframe);
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
