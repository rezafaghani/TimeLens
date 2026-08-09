namespace TimeLens.Domain.Models;

public class DatasetMetadataDto
{
    public string Id { get; set; } = string.Empty;
    public string SeriesId { get; set; } = string.Empty;
    public string Provider { get; set; } = "coinbase-exchange";
    public string Exchange { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string AssetClass { get; set; } = "Crypto";
    public string BaseAsset { get; set; } = string.Empty;
    public string QuoteAsset { get; set; } = string.Empty;
    public string MarketDataType { get; set; } = "ohlcv";
    public string Timeframe { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public string ProviderInstrumentId { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Unit { get; set; } = "price";
    public string Calendar { get; set; } = "crypto-24x7";
    public string LicenseInfo { get; set; } = string.Empty;
    public bool Deprecated { get; set; }
    public Dictionary<string, string> RequestParameters { get; set; } = [];
    public Dictionary<string, string> ProviderMetadata { get; set; } = [];
    public Dictionary<string, string> UserMetadata { get; set; } = [];
    public DateTimeOffset? FirstAvailableAt { get; set; }
    public DateTimeOffset? LastAvailableAt { get; set; }
    public DateTimeOffset FirstObservedAt { get; set; }
    public DateTimeOffset LastIngestedAt { get; set; }
}
