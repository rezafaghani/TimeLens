using TimeLens.Domain.Models;

namespace TimeLens.Domain.Interfaces;

public interface IIngestionControlRepository
{
    Task<List<IngestionSchedule>> GetEnabledSchedulesAsync(CancellationToken cancellationToken = default);
    Task<List<IngestionSchedule>> GetSchedulesAsync(long? seriesId = null, CancellationToken cancellationToken = default);
    Task<IngestionSchedule?> GetScheduleAsync(string id, CancellationToken cancellationToken = default);
    Task<List<IngestionJob>> GetJobsAsync(string? scheduleId, long? seriesId, CancellationToken cancellationToken = default);
    Task<List<IngestionExecution>> GetExecutionsAsync(string? jobId, string? scheduleId, long? seriesId, CancellationToken cancellationToken = default);
    Task EnsureDefaultSchedulesAsync(IEnumerable<IngestionSchedule> schedules, CancellationToken cancellationToken = default);
    Task<IngestionSchedule> CreateScheduleAsync(IngestionSchedule schedule, CancellationToken cancellationToken = default);
    Task<IngestionSchedule?> UpdateScheduleAsync(IngestionSchedule schedule, CancellationToken cancellationToken = default);
    Task<IngestionSchedule?> ResetScheduleAsync(string id, CancellationToken cancellationToken = default);
    Task<IngestionJobMessage?> TryCreateQueuedJobAsync(IngestionSchedule schedule, DateTimeOffset queuedAt, CancellationToken cancellationToken = default);
    Task<IngestionJobMessage> CreateBackloadJobAsync(IngestionSchedule schedule, string endpoint, string source, Dictionary<string, string> parameters, string windowStartExpression, string windowEndExpression, int batchSize, DateTimeOffset queuedAt, CancellationToken cancellationToken = default);
    Task<IngestionJobMessage?> TryCreateBackloadJobAsync(IngestionSchedule schedule, string endpoint, string source, Dictionary<string, string> parameters, string windowStartExpression, string windowEndExpression, int batchSize, string jobId, DateTimeOffset queuedAt, CancellationToken cancellationToken = default);
    Task MarkJobRunningAsync(string jobId, string executionId, CancellationToken cancellationToken = default);
    Task MarkJobArtifactsQueuedAsync(string jobId, string executionId, int artifactCount, CancellationToken cancellationToken = default);
    Task MarkJobFailedAsync(string jobId, string executionId, string error, CancellationToken cancellationToken = default);
}
