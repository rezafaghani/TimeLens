namespace TimeLens.Domain.Entities;

public class IngestionExecution
{
    public string Id { get; set; } = string.Empty;

    public string JobId { get; set; } = string.Empty;
    public string ScheduleId { get; set; } = string.Empty;
    public string SeriesId { get; set; } = string.Empty;
    public string Status { get; set; } = IngestionStatuses.Queued;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int Inserted { get; set; }
    public int Skipped { get; set; }
    public string Error { get; set; } = string.Empty;
}
