namespace TimeLens.Domain.Entities;

public class IngestionSchedule
{
    public string Id { get; set; } = string.Empty;

    public string SeriesId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string CronExpression { get; set; } = string.Empty;
    public string DefaultCronExpression { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "coinbase-exchange";
    public string Endpoint { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = [];
    public int LookbackHours { get; set; } = 48;
    public string WindowStartExpression { get; set; } = "now-48h";
    public string WindowEndExpression { get; set; } = "now";
    public string DefaultWindowStartExpression { get; set; } = "now-48h";
    public string DefaultWindowEndExpression { get; set; } = "now";
    public int BatchSize { get; set; } = 500;
    public DateTimeOffset? LastQueuedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
