using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orleans;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Observability;
using TimeLens.Ingestion.Services;

namespace TimeLens.Ingestion.Grains;

public class CoinbaseCandleGrain(
    CoinbaseCandleClient coinbaseClient,
    CoinbaseCandleNormalizer normalizer,
    IngestionWriteClient insertClient,
    IServiceScopeFactory scopeFactory,
    IOptions<IngestionOptions> options,
    ILogger<CoinbaseCandleGrain> logger) : Grain, ICoinbaseCandleGrain
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
        var runningRepository = runningScope.ServiceProvider.GetRequiredService<IIngestionControlRepository>();
        await runningRepository.MarkJobRunningAsync(message.JobId, message.ExecutionId, cancellationToken);

        var inserted = 0;
        var skipped = 0;

        try
        {
            var productId = Get(message.Parameters, "product_id");
            var timeframe = Get(message.Parameters, "timeframe", "1h");
            var granularity = GranularitySeconds(timeframe);
            var start = Resolve(message.WindowStartExpression, -Math.Max(message.LookbackHours > 0 ? message.LookbackHours : options.Value.LookbackHours, 1));
            var end = Resolve(message.WindowEndExpression, 0);
            activity?.SetTag("market.timeframe", timeframe);
            activity?.SetTag("timelens.window_start", start.ToUnixTimeSeconds());
            activity?.SetTag("timelens.window_end", end.ToUnixTimeSeconds());
            if (start >= end)
            {
                throw new ArgumentException("A valid half-open ingestion window is required.");
            }

            foreach (var (chunkStart, chunkEnd) in Chunks(start, end, granularity))
            {
                var definition = new MarketDataRequestDefinition
                {
                    Endpoint = message.Endpoint,
                    Parameters = new Dictionary<string, string>(message.Parameters)
                    {
                        ["product_id"] = productId,
                        ["timeframe"] = timeframe,
                        ["granularity"] = granularity.ToString()
                    }
                };

                using var providerActivity = TimeLensTelemetry.ActivitySource.StartActivity("ProviderRequest");
                var providerStarted = Stopwatch.GetTimestamp();
                providerActivity?.SetTag("market.provider", message.Source);
                providerActivity?.SetTag("market.symbol", productId);
                providerActivity?.SetTag("market.timeframe", timeframe);
                using var document = await coinbaseClient.GetAsync(productId, granularity, chunkStart, chunkEnd, cancellationToken);
                TimeLensTelemetry.ProviderRequestDuration.Record(Stopwatch.GetElapsedTime(providerStarted).TotalSeconds, KeyValuePair.Create<string, object?>("market.provider", message.Source));
                NormalizedDataset dataset;
                using (TimeLensTelemetry.ActivitySource.StartActivity("NormalizeMarketData"))
                {
                    dataset = normalizer.Normalize(definition, document.RootElement);
                }
                dataset.Metadata.SeriesId = message.SeriesId;

                using var metadataScope = scopeFactory.CreateScope();
                var metadataRepository = metadataScope.ServiceProvider.GetRequiredService<IDatasetRepository>();
                var saved = await metadataRepository.UpsertAsync(dataset.Metadata, cancellationToken);
                dataset.Batch.DatasetId = saved.Id;
                dataset.Batch.SourceMetadataVersion = saved.LastIngestedAt.ToUnixTimeSeconds().ToString();

                foreach (var chunk in dataset.Batch.Points.Chunk(Math.Max(message.BatchSize > 0 ? message.BatchSize : options.Value.BatchSize, 1)))
                {
                    var batch = new TimeSeriesBatchRequest
                    {
                        DatasetId = dataset.Batch.DatasetId,
                        SourceMetadataVersion = dataset.Batch.SourceMetadataVersion,
                        Points = chunk.ToList()
                    };
                    TimeSeriesInsertResult result;
                    using (TimeLensTelemetry.ActivitySource.StartActivity("PersistMarketData"))
                    {
                        result = await insertClient.InsertBatchAsync(batch, cancellationToken);
                    }
                    inserted += result.Inserted;
                    skipped += result.Skipped;
                    logger.LogInformation(
                        "Inserted {Inserted}, skipped {Skipped} for {Provider} {SeriesId} {Timeframe}.",
                        result.Inserted,
                        result.Skipped,
                        saved.Provider,
                        saved.SeriesId,
                        saved.Timeframe);
                }
            }

            using var completedScope = scopeFactory.CreateScope();
            var completedRepository = completedScope.ServiceProvider.GetRequiredService<IIngestionControlRepository>();
            await completedRepository.MarkJobCompletedAsync(message.JobId, message.ExecutionId, inserted, skipped, cancellationToken);
            activity?.SetTag("timelens.records_inserted", inserted);
            activity?.SetTag("timelens.records_skipped", skipped);
            TimeLensTelemetry.IngestionExecutions.Add(1, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            TimeLensTelemetry.RecordsIngested.Add(inserted, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            TimeLensTelemetry.IngestionDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, KeyValuePair.Create<string, object?>("market.provider", message.Source));
        }
        catch (Exception ex)
        {
            activity.RecordException(ex);
            TimeLensTelemetry.IngestionFailures.Add(1, KeyValuePair.Create<string, object?>("market.provider", message.Source));
            using var failedScope = scopeFactory.CreateScope();
            var failedRepository = failedScope.ServiceProvider.GetRequiredService<IIngestionControlRepository>();
            await failedRepository.MarkJobFailedAsync(message.JobId, message.ExecutionId, ex.Message, CancellationToken.None);
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

    private static int GranularitySeconds(string timeframe) => timeframe switch
    {
        "1m" => 60,
        "5m" => 300,
        "15m" => 900,
        "1h" => 3600,
        "1d" => 86400,
        _ => throw new ArgumentException($"Unsupported timeframe '{timeframe}'.")
    };

    private static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Chunks(DateTimeOffset start, DateTimeOffset end, int granularitySeconds)
    {
        var maxSpan = TimeSpan.FromSeconds(granularitySeconds * 300);
        for (var chunkStart = start; chunkStart < end;)
        {
            var chunkEnd = chunkStart.Add(maxSpan);
            if (chunkEnd > end)
            {
                chunkEnd = end;
            }

            yield return (chunkStart, chunkEnd);
            chunkStart = chunkEnd;
        }
    }
}
