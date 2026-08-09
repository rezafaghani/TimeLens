using System.Text.Json;

namespace TimeLens.Domain.Models;

public static class QualityStatuses
{
    public const string Healthy = "healthy";
    public const string Warning = "warning";
    public const string Degraded = "degraded";
    public const string Critical = "critical";
    public const string Unknown = "unknown";
    public const string Skipped = "skipped";
    public const string ExecutionError = "execution_error";
    public const string InsufficientData = "insufficient_data";
    public const string MarketClosed = "market_closed";
}

public static class QualityExecutionStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithFindings = "completed_with_findings";
    public const string CompletedWithPartialFailures = "completed_with_partial_failures";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

public record QualityValidatorTypeDto(
    string Id,
    string Category,
    string DisplayName,
    string Description,
    string TargetType,
    int ConfigurationVersion,
    string DefaultSeverity,
    JsonElement ConfigurationSchema);

[Flags]
public enum ValidationPluginUsage
{
    None = 0,
    Api = 1,
    Scheduler = 2,
    Worker = 4
}

public record RegisteredValidationPluginDto(
    string Id,
    string Category,
    string DisplayName,
    string Description,
    string TargetType,
    int ConfigurationVersion,
    string DefaultSeverity,
    JsonElement ConfigurationSchema,
    ValidationPluginUsage Usage,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt);

public record QualitySeriesGroupDto(
    string Id,
    string Name,
    string Description,
    string GroupType,
    bool Enabled,
    JsonElement Rule,
    JsonElement Tags,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record QualitySeriesGroupMemberDto(
    string GroupId,
    string DatasetId,
    string SeriesId,
    DateTimeOffset CreatedAt);

public record ReplaceQualitySeriesGroupMembersRequest(List<QualitySeriesGroupMemberRequest> Members);

public record QualitySeriesGroupMemberRequest(string DatasetId, string SeriesId);

public record UpsertQualitySeriesGroupRequest(
    string? Id,
    string Name,
    string? Description,
    string GroupType,
    bool Enabled,
    JsonElement? Rule,
    JsonElement? Tags);

public record QualityValidationJobDto(
    string Id,
    string Name,
    string Description,
    bool Enabled,
    string CronExpression,
    string TimeZone,
    string WindowStartExpression,
    string WindowEndExpression,
    int MaxParallelism,
    int TimeoutSeconds,
    JsonElement Tags,
    List<QualityValidationJobTargetDto> Targets,
    List<QualityValidationJobCheckDto> Checks,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastQueuedAt);

public record QualityValidationJobTargetDto(
    string TargetType,
    string TargetId,
    JsonElement Rule);

public record QualityValidationJobCheckDto(
    string Id,
    string ValidatorId,
    int ValidatorVersion,
    bool Enabled,
    JsonElement Configuration,
    JsonElement Severity,
    int SortOrder);

public record UpsertQualityValidationJobRequest(
    string? Id,
    string Name,
    string? Description,
    bool Enabled,
    string CronExpression,
    string TimeZone,
    string WindowStartExpression,
    string WindowEndExpression,
    int? MaxParallelism,
    int? TimeoutSeconds,
    JsonElement? Tags,
    List<UpsertQualityValidationJobTargetRequest> Targets,
    List<UpsertQualityValidationJobCheckRequest> Checks);

public record UpsertQualityValidationJobTargetRequest(
    string TargetType,
    string TargetId,
    JsonElement? Rule);

public record UpsertQualityValidationJobCheckRequest(
    string? Id,
    string ValidatorId,
    int? ValidatorVersion,
    bool Enabled,
    JsonElement? Configuration,
    JsonElement? Severity,
    int? SortOrder);

public record QualityFindingDto(
    string Id,
    string ExecutionId,
    string? TargetExecutionId,
    string? ValidatorExecutionId,
    string DatasetId,
    string SeriesId,
    string ValidatorId,
    string Category,
    string Severity,
    string QualityStatus,
    string TradingImpact,
    string Title,
    string Message,
    DateTimeOffset? AffectedStart,
    DateTimeOffset? AffectedEnd,
    int? ExpectedCount,
    int? ActualCount,
    int? AffectedCount,
    JsonElement SampleTimestamps,
    JsonElement Details,
    string Fingerprint,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public record QualityStatusDto(
    string DatasetId,
    string SeriesId,
    string OverallStatus,
    JsonElement CategoryStatuses,
    string LatestExecutionId,
    DateTimeOffset AsOf);

public record QualityExecutionDto(
    string Id,
    string JobId,
    string TriggerType,
    string Status,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    DateTimeOffset? EvaluatedStart,
    DateTimeOffset? EvaluatedEnd,
    int TargetCount,
    int CompletedCount,
    int WarningCount,
    int CriticalCount,
    int TechnicalFailureCount,
    string Error);

public record QualitySummaryDto(
    int Healthy,
    int Degraded,
    int Critical,
    int Unknown,
    int ActiveFindings,
    int ActiveCriticalFindings);

public record QualityEvaluationRequest(
    DatasetMetadataDto Metadata,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset Now,
    TimeSpan Granularity,
    TimeSpan? AllowedDelay,
    double? MinimumValue,
    double? MaximumValue,
    double? MaximumAbsoluteChange,
    double? MaximumPercentageChange,
    double NearZeroFloor,
    int FlatLinePointCount,
    List<TimeSeriesPointDto> Points);

public record QualityFindingDraftDto(
    string ValidatorId,
    string Category,
    string Severity,
    string QualityStatus,
    string Title,
    string Message,
    DateTimeOffset? AffectedStart,
    DateTimeOffset? AffectedEnd,
    int? ExpectedCount,
    int? ActualCount,
    int? AffectedCount,
    List<DateTimeOffset> SampleTimestamps,
    JsonElement? Details = null);

public record ManualQualityEvaluationRequest(
    string DatasetId,
    string Start,
    string End,
    string? AsOf,
    string? TimeZone,
    string? Granularity,
    string? AllowedDelay,
    double? MinimumValue,
    double? MaximumValue,
    double? MaximumAbsoluteChange,
    double? MaximumPercentageChange,
    double? NearZeroFloor,
    int? FlatLinePointCount);

public record ManualQualityEvaluationResult(
    DatasetMetadataDto Metadata,
    DateTimeOffset Start,
    DateTimeOffset End,
    int PointCount,
    string OverallStatus,
    List<QualityFindingDraftDto> Findings,
    string? ExecutionId = null);

public record RunQualityJobRequest(string? Start, string? End, string? TriggerType = null);

public record RunQualityJobResult(
    string JobId,
    string TriggerType,
    int TargetCount,
    int CompletedCount,
    int FindingCount,
    int CriticalCount,
    List<ManualQualityEvaluationResult> Results);
