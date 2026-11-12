using Cronos;
using TimeLens.Domain.Entities;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Infrastructure.Services;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;

namespace TimeLens.Scheduler;

public class Worker(
    IServiceScopeFactory scopeFactory,
    RabbitMqJobPublisher publisher,
    IHttpClientFactory httpClientFactory,
    IOptions<SchedulerOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await QueueDueSchedulesAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(options.Value.PollingSeconds, 5)), stoppingToken);
        }
    }

    private async Task QueueDueSchedulesAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IIngestionControlRepository>();
        var now = DateTimeOffset.UtcNow;

        var schedules = await repository.GetEnabledSchedulesAsync(cancellationToken);
        await EnsureAutomaticValidationJobsAsync(scope.ServiceProvider, schedules, cancellationToken);

        foreach (var schedule in schedules)
        {
            try
            {
                if (!IsDue(schedule, now))
                {
                    continue;
                }

                var message = await repository.TryCreateQueuedJobAsync(schedule, now, cancellationToken);
                if (message is null)
                {
                    continue;
                }

                await publisher.PublishAsync(message, cancellationToken);
                logger.LogInformation("Queued ingestion job {JobId} for schedule {ScheduleId} and series {SeriesId}.",
                    message.JobId,
                    message.ScheduleId,
                    message.SeriesId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to queue schedule {ScheduleId}.", schedule.Id);
            }
        }

        await QueueDueExecutionDefinitionsAsync(scope.ServiceProvider, now, cancellationToken);
        await QueueDueQualityJobsAsync(scope.ServiceProvider, now, cancellationToken);
    }

    private static bool IsDue(IngestionSchedule schedule, DateTimeOffset now)
    {
        var from = schedule.LastQueuedAt ?? schedule.CreatedAt.AddMinutes(-1);
        var expression = CronExpression.Parse(schedule.CronExpression);
        var next = expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
        return next.HasValue && next.Value <= now;
    }

    private async Task QueueDueExecutionDefinitionsAsync(IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.ExecutionApiBaseUrl))
        {
            return;
        }

        var repository = services.GetRequiredService<IExecutionRepository>();
        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(options.Value.ExecutionApiBaseUrl.TrimEnd('/') + "/");

        foreach (var definition in await repository.GetEnabledDefinitionsAsync(cancellationToken))
        {
            try
            {
                if (!IsDue(definition, now))
                {
                    continue;
                }

                var response = await client.PostAsJsonAsync(
                    $"api/execution-definitions/{Uri.EscapeDataString(definition.Id)}/runs",
                    new RunExecutionDefinitionRequest(null, null, "scheduled"),
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Execution definition {DefinitionId} returned {StatusCode}.", definition.Id, response.StatusCode);
                    continue;
                }

                await repository.MarkDefinitionQueuedAsync(definition.Id, now, cancellationToken);
                logger.LogInformation("Queued execution definition {DefinitionId}.", definition.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to queue execution definition {DefinitionId}.", definition.Id);
            }
        }
    }

    private async Task QueueDueQualityJobsAsync(IServiceProvider services, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IQualityRepository>();
        var ingestionRepository = services.GetRequiredService<IIngestionControlRepository>();

        foreach (var job in await repository.GetEnabledJobsAsync(cancellationToken))
        {
            try
            {
                var hasInitialData = job.LastQueuedAt is null
                    && await HasIngestedDataAsync(job, ingestionRepository, cancellationToken);
                if (!hasInitialData && !IsDue(job, now))
                {
                    continue;
                }

                var message = new ValidationJobMessage
                {
                    JobId = job.Id,
                    ExecutionId = $"quality-execution-{Guid.NewGuid():N}",
                    TriggerType = hasInitialData ? "first_data" : "scheduled",
                    WindowStartExpression = job.WindowStartExpression,
                    WindowEndExpression = job.WindowEndExpression
                };
                await publisher.PublishValidationAsync(message, cancellationToken);
                await repository.MarkJobQueuedAsync(job.Id, now, cancellationToken);
                logger.LogInformation("Queued validation job {JobId}.", job.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to queue validation job {JobId}.", job.Id);
            }
        }
    }

    private async Task EnsureAutomaticValidationJobsAsync(
        IServiceProvider services,
        IEnumerable<TimeLens.Domain.Entities.IngestionSchedule> schedules,
        CancellationToken cancellationToken)
    {
        var repository = services.GetRequiredService<IQualityRepository>();
        foreach (var schedule in schedules)
        {
            try
            {
                var request = DefaultQualityValidationJobs.Create(schedule);
                if (await repository.GetJobAsync(request.Id!, cancellationToken) is null)
                {
                    await repository.UpsertJobAsync(request, cancellationToken);
                    logger.LogInformation("Created automatic validation for series {SeriesId}.", schedule.SeriesId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to configure automatic validation for series {SeriesId}.", schedule.SeriesId);
            }
        }
    }

    private static async Task<bool> HasIngestedDataAsync(
        QualityValidationJobDto job,
        IIngestionControlRepository ingestionRepository,
        CancellationToken cancellationToken)
    {
        foreach (var target in job.Targets.Where(target =>
                     target.TargetType.Equals("series", StringComparison.OrdinalIgnoreCase)))
        {
            var seriesId = long.Parse(target.TargetId, System.Globalization.CultureInfo.InvariantCulture);
            var executions = await ingestionRepository.GetExecutionsAsync(null, null, seriesId, cancellationToken);
            if (DefaultQualityValidationJobs.HasIngestedData(executions))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDue(ExecutionDefinitionDto definition, DateTimeOffset now)
    {
        var from = definition.LastQueuedAt ?? definition.CreatedAt.AddMinutes(-1);
        var expression = CronExpression.Parse(definition.CronExpression);
        var next = expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
        return next.HasValue && next.Value <= now;
    }

    private static bool IsDue(QualityValidationJobDto job, DateTimeOffset now)
    {
        var from = job.LastQueuedAt ?? job.CreatedAt.AddMinutes(-1);
        var expression = CronExpression.Parse(job.CronExpression);
        var next = expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
        return next.HasValue && next.Value <= now;
    }
}
