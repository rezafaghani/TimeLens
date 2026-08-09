namespace TimeLens.Domain.Models;

public class DatasetSearchFilter
{
    public string? Search { get; set; }
    public string? SeriesId { get; set; }
    public string? Provider { get; set; }
    public string? Exchange { get; set; }
    public string? Symbol { get; set; }
    public string? AssetClass { get; set; }
    public string? BaseAsset { get; set; }
    public string? QuoteAsset { get; set; }
    public string? MarketDataType { get; set; }
    public string? Timeframe { get; set; }
    public string? Endpoint { get; set; }
}
