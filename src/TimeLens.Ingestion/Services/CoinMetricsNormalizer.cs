using System.Globalization;
using System.Text.Json;
using TimeLens.Domain.Models;

namespace TimeLens.Ingestion.Services;

public class CoinMetricsNormalizer
{
    public NormalizedDataset Normalize(MarketDataRequestDefinition definition, JsonElement root)
    {
        var asset = Parameter(definition, "asset", "btc");
        var metric = Parameter(definition, "metric");
        var frequency = Parameter(definition, "frequency", "1d");
        var metadata = new DatasetMetadataDto
        {
            SeriesId = $"coinmetrics:{asset}:{metric}:{frequency}".ToLowerInvariant(),
            Provider = "coin-metrics-community",
            Exchange = "Bitcoin network",
            Symbol = $"{asset.ToUpperInvariant()}-{metric}",
            AssetClass = "Crypto",
            BaseAsset = asset.ToUpperInvariant(),
            MarketDataType = "network-metric",
            Timeframe = frequency,
            TimeZone = "UTC",
            Calendar = "crypto-24x7",
            ProviderInstrumentId = metric,
            Endpoint = definition.Endpoint.Trim('/'),
            Unit = Unit(metric),
            LicenseInfo = "Coin Metrics Community",
            RequestParameters = new Dictionary<string, string>(definition.Parameters),
            ProviderMetadata = new Dictionary<string, string> { ["metric"] = metric }
        };

        var points = new List<TimeSeriesWritePoint>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("time", out var timeElement)
                    || !item.TryGetProperty(metric, out var valueElement)
                    || !DateTimeOffset.TryParse(timeElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                    || !TryReadDouble(valueElement, out var value))
                {
                    continue;
                }

                points.Add(new TimeSeriesWritePoint
                {
                    Timestamp = timestamp,
                    Open = value,
                    High = value,
                    Low = value,
                    Close = value,
                    Volume = 0
                });
            }
        }

        points = points.OrderBy(x => x.Timestamp).ToList();
        if (points.Count > 0)
        {
            metadata.FirstAvailableAt = points[0].Timestamp;
            metadata.LastAvailableAt = points[^1].Timestamp;
        }

        return new NormalizedDataset
        {
            Metadata = metadata,
            Batch = new TimeSeriesBatchRequest { Points = points }
        };
    }

    public static string Unit(string metric) => metric switch
    {
        "AdrActCnt" => "Addresses",
        "TxCnt" => "Transactions",
        _ => "Varies"
    };

    private static string Parameter(MarketDataRequestDefinition definition, string key, string fallback = "") =>
        definition.Parameters.TryGetValue(key, out var value) ? value : fallback;

    private static bool TryReadDouble(JsonElement value, out double result) =>
        value.ValueKind == JsonValueKind.Number
            ? value.TryGetDouble(out result)
            : double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
}
