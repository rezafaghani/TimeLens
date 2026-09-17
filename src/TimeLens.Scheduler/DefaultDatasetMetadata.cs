using TimeLens.Domain.Entities;
using TimeLens.Domain.Models;

namespace TimeLens.Scheduler;

public static class DefaultDatasetMetadata
{
    public static IEnumerable<DatasetMetadataDto> Create(IEnumerable<IngestionSchedule> schedules)
    {
        foreach (var schedule in schedules)
        {
            if (schedule.Source == "energy-charts")
            {
                var biddingZone = Parameter(schedule, "bzn");
                yield return new DatasetMetadataDto
                {
                    SeriesId = schedule.SeriesId,
                    Provider = schedule.Source,
                    Exchange = "European day-ahead electricity market",
                    Symbol = biddingZone,
                    AssetClass = "Energy",
                    BaseAsset = "Electricity",
                    QuoteAsset = "EUR",
                    Currency = "EUR",
                    MarketDataType = "price",
                    Timeframe = Parameter(schedule, "timeframe", "1h"),
                    TimeZone = "Europe/Copenhagen",
                    Calendar = "electricity-day-ahead",
                    ProviderInstrumentId = biddingZone,
                    Endpoint = schedule.Endpoint,
                    Unit = "EUR / MWh",
                    RequestParameters = new Dictionary<string, string>(schedule.Parameters)
                };
                continue;
            }

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

    private static string Parameter(IngestionSchedule schedule, string key, string fallback = "") =>
        schedule.Parameters.TryGetValue(key, out var value) ? value : fallback;
}
