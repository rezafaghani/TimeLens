namespace TimeLens.Domain.Models;

public class TimeSeriesBatchRequest
{
    public long SeriesId { get; set; }
    public string SourceMetadataVersion { get; set; } = string.Empty;
    public List<TimeSeriesWritePoint> Points { get; set; } = [];
}
