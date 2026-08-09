using TimeLens.Domain.Entities;
using TimeLens.Domain.Models;

namespace TimeLens.Scheduler;

public static class DefaultDatasetMetadata
{
    public static IEnumerable<DatasetMetadataDto> Create(IEnumerable<IngestionSchedule> schedules)
    {
        foreach (var schedule in schedules)
        {
            var productId = Parameter(schedule, "product_id");
            var parts = productId.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
            var quoteAsset = parts.ElementAtOrDefault(1) ?? string.Empty;
            yield return new DatasetMetadataDto
            {
                SeriesId = schedule.SeriesId,
                Provider = schedule.Source,
                Exchange = "Coinbase",
                Symbol = productId,
                AssetClass = "Crypto",
                BaseAsset = parts.ElementAtOrDefault(0) ?? string.Empty,
                QuoteAsset = quoteAsset,
                Currency = quoteAsset,
                MarketDataType = "ohlcv",
                Timeframe = Parameter(schedule, "timeframe"),
                TimeZone = "UTC",
                Calendar = "crypto-24x7",
                ProviderInstrumentId = productId,
                Endpoint = schedule.Endpoint,
                Unit = quoteAsset,
                RequestParameters = new Dictionary<string, string>(schedule.Parameters)
            };
        }
    }

    private static string Parameter(IngestionSchedule schedule, string key) =>
        schedule.Parameters.TryGetValue(key, out var value) ? value : string.Empty;
}
