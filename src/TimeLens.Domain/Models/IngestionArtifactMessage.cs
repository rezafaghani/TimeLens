namespace TimeLens.Domain.Models;

public class IngestionArtifactMessage
{
    public Guid IngestionId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public long SeriesId { get; set; }
    public string ProviderSeriesId { get; set; } = string.Empty;
    public DateTimeOffset IngestedAt { get; set; }
    public string RawObjectKey { get; set; } = string.Empty;
    public string ParsedObjectKey { get; set; } = string.Empty;
    public string ParsedSha256 { get; set; } = string.Empty;
    public string SourceMetadataVersion { get; set; } = string.Empty;
    public int PointCount { get; set; }
    public int RetryCount { get; set; }
    public int ArtifactCount { get; set; }
}
