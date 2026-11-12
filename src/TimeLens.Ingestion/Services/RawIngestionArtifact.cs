namespace TimeLens.Ingestion.Services;

public class RawIngestionArtifact
{
    public Guid IngestionId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public long SeriesId { get; set; }
    public string ProviderSeriesId { get; set; } = string.Empty;
    public DateTimeOffset IngestedAt { get; set; }
    public string RawObjectKey { get; set; } = string.Empty;
    public int ArtifactCount { get; set; }
}
