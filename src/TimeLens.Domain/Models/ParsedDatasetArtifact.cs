namespace TimeLens.Domain.Models;

public class ParsedDatasetArtifact
{
    public long SeriesId { get; set; }
    public List<TimeSeriesWritePoint> Points { get; set; } = [];
}
