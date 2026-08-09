namespace TimeLens.Domain.Models;

public class MarketDataUpdatedEvent
{
    public string EventId { get; set; } = $"market-data-updated-{Guid.NewGuid():N}";
    public int Version { get; set; } = 1;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string DatasetId { get; set; } = string.Empty;
    public string SeriesId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string InstrumentId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string MarketDataType { get; set; } = "ohlcv";
    public string Timeframe { get; set; } = string.Empty;
    public string Operation { get; set; } = "upsert";
    public DateTimeOffset DataTimestamp { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public TimeSeriesWritePoint Bar { get; set; } = new();
}
