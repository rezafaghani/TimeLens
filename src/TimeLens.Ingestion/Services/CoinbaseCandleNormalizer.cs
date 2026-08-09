using System.Globalization;
using System.Text.Json;
using TimeLens.Domain.Models;

namespace TimeLens.Ingestion.Services;

public class CoinbaseCandleNormalizer
{
    public NormalizedDataset Normalize(MarketDataRequestDefinition definition, JsonElement root)
    {
        var productId = GetParameter(definition, "product_id");
        var timeframe = GetParameter(definition, "timeframe", "1h");
        var parts = productId.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
        var baseAsset = parts.ElementAtOrDefault(0) ?? string.Empty;
        var quoteAsset = parts.ElementAtOrDefault(1) ?? string.Empty;

        var metadata = new DatasetMetadataDto
        {
            SeriesId = $"coinbase:{productId}:{timeframe}".ToLowerInvariant(),
            Provider = "coinbase-exchange",
            Exchange = "Coinbase",
            Symbol = productId,
            AssetClass = "Crypto",
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            Currency = quoteAsset,
            MarketDataType = "ohlcv",
            Timeframe = timeframe,
            TimeZone = "UTC",
            Calendar = "crypto-24x7",
            ProviderInstrumentId = productId,
            Endpoint = definition.Endpoint.Trim('/'),
            Unit = quoteAsset,
            RequestParameters = new Dictionary<string, string>(definition.Parameters),
            ProviderMetadata = new Dictionary<string, string>
            {
                ["granularitySeconds"] = GetParameter(definition, "granularity")
            }
        };

        var points = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Array && x.GetArrayLength() >= 6)
                .Select(x => new TimeSeriesWritePoint
                {
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(x[0].GetInt64()),
                    Low = ReadDouble(x[1]),
                    High = ReadDouble(x[2]),
                    Open = ReadDouble(x[3]),
                    Close = ReadDouble(x[4]),
                    Volume = ReadDouble(x[5])
                })
                .OrderBy(x => x.Timestamp)
                .ToList()
            : [];

        if (points.Count > 0)
        {
            metadata.FirstAvailableAt = points.First().Timestamp;
            metadata.LastAvailableAt = points.Last().Timestamp;
        }

        return new NormalizedDataset
        {
            Metadata = metadata,
            Batch = new TimeSeriesBatchRequest { Points = points }
        };
    }

    private static string GetParameter(MarketDataRequestDefinition definition, string key, string fallback = "") =>
        definition.Parameters.TryGetValue(key, out var value) ? value : fallback;

    private static double ReadDouble(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? double.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture) : value.GetDouble();
}
