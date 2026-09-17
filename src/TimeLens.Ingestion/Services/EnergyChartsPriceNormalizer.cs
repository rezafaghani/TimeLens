using System.Globalization;
using System.Text.Json;
using TimeLens.Domain.Models;

namespace TimeLens.Ingestion.Services;

public class EnergyChartsPriceNormalizer
{
    public NormalizedDataset Normalize(MarketDataRequestDefinition definition, JsonElement root)
    {
        var biddingZone = GetParameter(definition, "bzn", "DK1");
        var timeframe = GetParameter(definition, "timeframe", "1h");
        var unit = root.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() ?? "EUR / MWh" : "EUR / MWh";
        var license = root.TryGetProperty("license_info", out var licenseElement) ? licenseElement.GetString() ?? string.Empty : string.Empty;
        var deprecated = root.TryGetProperty("deprecated", out var deprecatedElement) && deprecatedElement.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? deprecatedElement.GetBoolean().ToString(CultureInfo.InvariantCulture)
            : "False";

        var metadata = new DatasetMetadataDto
        {
            SeriesId = $"energy-charts:{biddingZone}:day-ahead-price".ToLowerInvariant(),
            Provider = "energy-charts",
            Exchange = "European day-ahead electricity market",
            Symbol = biddingZone,
            AssetClass = "Energy",
            BaseAsset = "Electricity",
            QuoteAsset = "EUR",
            Currency = "EUR",
            MarketDataType = "price",
            Timeframe = timeframe,
            TimeZone = "Europe/Copenhagen",
            Calendar = "electricity-day-ahead",
            ProviderInstrumentId = biddingZone,
            Endpoint = definition.Endpoint.Trim('/'),
            Unit = unit,
            LicenseInfo = license,
            RequestParameters = new Dictionary<string, string>(definition.Parameters),
            ProviderMetadata = new Dictionary<string, string>
            {
                ["deprecated"] = deprecated
            }
        };

        var points = new List<TimeSeriesWritePoint>();
        if (root.TryGetProperty("unix_seconds", out var timestamps)
            && root.TryGetProperty("price", out var prices)
            && timestamps.ValueKind == JsonValueKind.Array
            && prices.ValueKind == JsonValueKind.Array)
        {
            var timestampValues = timestamps.EnumerateArray().ToList();
            var priceValues = prices.EnumerateArray().ToList();
            for (var i = 0; i < Math.Min(timestampValues.Count, priceValues.Count); i++)
            {
                var price = ReadDouble(priceValues[i]);
                points.Add(new TimeSeriesWritePoint
                {
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(timestampValues[i].GetInt64()),
                    Open = price,
                    High = price,
                    Low = price,
                    Close = price,
                    Volume = 0
                });
            }
        }

        points = points.OrderBy(x => x.Timestamp).ToList();
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
