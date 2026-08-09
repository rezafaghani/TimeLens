namespace TimeLens.Domain.Models;

public class IngestionJobMessage
{
    public string ScheduleId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public string SeriesId { get; set; } = string.Empty;
    public string Source { get; set; } = "coinbase-exchange";
    public string Endpoint { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = [];
    public int LookbackHours { get; set; }
    public string WindowStartExpression { get; set; } = string.Empty;
    public string WindowEndExpression { get; set; } = string.Empty;
    public int BatchSize { get; set; }
}
