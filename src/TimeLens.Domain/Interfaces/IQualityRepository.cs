using TimeLens.Domain.Models;

namespace TimeLens.Domain.Interfaces;

public interface IQualityRepository
{
    Task<List<QualitySeriesGroupDto>> GetSeriesGroupsAsync(CancellationToken cancellationToken = default);
    Task<List<QualityValidatorTypeDto>> GetValidatorTypesAsync(ValidationPluginUsage usage, CancellationToken cancellationToken = default);
    Task UpsertValidatorTypesAsync(IEnumerable<RegisteredValidationPluginDto> validators, CancellationToken cancellationToken = default);
    Task<QualitySeriesGroupDto> UpsertSeriesGroupAsync(UpsertQualitySeriesGroupRequest request, CancellationToken cancellationToken = default);
    Task<QualitySeriesGroupDto?> SetSeriesGroupEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);
    Task<List<QualitySeriesGroupMemberDto>> GetSeriesGroupMembersAsync(string groupId, CancellationToken cancellationToken = default);
    Task<List<QualitySeriesGroupMemberDto>> ReplaceSeriesGroupMembersAsync(string groupId, ReplaceQualitySeriesGroupMembersRequest request, CancellationToken cancellationToken = default);
    Task<List<QualityValidationJobDto>> GetJobsAsync(CancellationToken cancellationToken = default);
    Task<List<QualityValidationJobDto>> GetEnabledJobsAsync(CancellationToken cancellationToken = default);
    Task<QualityValidationJobDto?> GetJobAsync(string id, CancellationToken cancellationToken = default);
    Task<List<QualityExecutionDto>> GetExecutionsAsync(string? jobId, string? seriesId, CancellationToken cancellationToken = default);
    Task<QualityValidationJobDto> UpsertJobAsync(UpsertQualityValidationJobRequest request, CancellationToken cancellationToken = default);
    Task<QualityValidationJobDto?> SetJobEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default);
    Task MarkJobQueuedAsync(string id, DateTimeOffset queuedAt, CancellationToken cancellationToken = default);
    Task<List<QualityFindingDto>> GetFindingsAsync(string? datasetId, string? seriesId, bool activeOnly, CancellationToken cancellationToken = default);
    Task<QualityStatusDto?> GetStatusAsync(string? datasetId, string? seriesId, CancellationToken cancellationToken = default);
    Task<QualitySummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
    Task<string> SaveManualEvaluationAsync(ManualQualityEvaluationResult result, CancellationToken cancellationToken = default);
    Task<string> SaveJobEvaluationAsync(string executionId, string jobId, string triggerType, ManualQualityEvaluationResult result, CancellationToken cancellationToken = default);
}
