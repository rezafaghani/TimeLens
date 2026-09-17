using Orleans;

namespace TimeLens.Domain.Models;

public class ValidationJobMessage
{
    public string JobId { get; set; } = string.Empty;
    public string ExecutionId { get; set; } = string.Empty;
    public string TriggerType { get; set; } = "scheduled";
    public string WindowStartExpression { get; set; } = string.Empty;
    public string WindowEndExpression { get; set; } = string.Empty;
}

// Orleans grain calls cross a serialization boundary even in the local development silo.
[GenerateSerializer]
public class ValidationCheckMessage
{
    [Id(0)]
    public string JobId { get; set; } = string.Empty;
    [Id(1)]
    public string ExecutionId { get; set; } = string.Empty;
    [Id(2)]
    public string ValidatorId { get; set; } = string.Empty;
    [Id(3)]
    public int ValidatorVersion { get; set; } = 1;
    [Id(4)]
    public string TargetType { get; set; } = string.Empty;
    [Id(5)]
    public string TargetId { get; set; } = string.Empty;
    [Id(6)]
    public DateTimeOffset Start { get; set; }
    [Id(7)]
    public DateTimeOffset End { get; set; }
    [Id(8)]
    public string ConfigurationJson { get; set; } = "{}";
}
