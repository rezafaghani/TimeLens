using System.Text;
using System.Text.Json;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Validation.Worker.Grains;
using Microsoft.Extensions.Options;
using Orleans;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TimeLens.Validation.Worker;

public class ValidationJobConsumer(
    IGrainFactory grainFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<ValidationJobConsumer> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "RabbitMQ validation listener failed. Retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var rabbitOptions = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = rabbitOptions.HostName,
            Port = rabbitOptions.Port,
            VirtualHost = rabbitOptions.VirtualHost,
            UserName = rabbitOptions.UserName,
            Password = rabbitOptions.Password
        };
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.ExchangeDeclareAsync(rabbitOptions.ExchangeName, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(
            rabbitOptions.ValidationQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: stoppingToken);
        await channel.QueueBindAsync(rabbitOptions.ValidationQueueName, rabbitOptions.ExchangeName, rabbitOptions.ValidationQueueName, cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(eventArgs.Body.ToArray());
                var message = JsonSerializer.Deserialize<ValidationJobMessage>(json, JsonOptions);
                if (message is not null)
                {
                    await RunJobAsync(message, stoppingToken);
                }

                await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process validation job message.");
                await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(rabbitOptions.ValidationQueueName, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Listening for validation jobs on RabbitMQ queue {QueueName}.", rabbitOptions.ValidationQueueName);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task RunJobAsync(ValidationJobMessage message, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var qualityRepository = scope.ServiceProvider.GetRequiredService<IQualityRepository>();
        var marketDataReader = scope.ServiceProvider.GetRequiredService<IMarketDataReader>();
        var job = await qualityRepository.GetJobAsync(message.JobId, cancellationToken)
            ?? throw new ArgumentException($"Validation job '{message.JobId}' was not found.");

        if (!DateTimeExpression.TryResolve(string.IsNullOrWhiteSpace(message.WindowStartExpression) ? job.WindowStartExpression : message.WindowStartExpression, out var start, job.TimeZone)
            || !DateTimeExpression.TryResolve(string.IsNullOrWhiteSpace(message.WindowEndExpression) ? job.WindowEndExpression : message.WindowEndExpression, out var end, job.TimeZone)
            || start >= end)
        {
            throw new ArgumentException("A valid half-open evaluation window is required.");
        }

        var targets = await ResolveTargets(job, qualityRepository, cancellationToken);
        for (var targetIndex = 0; targetIndex < targets.Count; targetIndex++)
        {
            var target = targets[targetIndex];
            var marketData = await marketDataReader.ReadAsync(target.TargetType, target.TargetId, start, end, null, cancellationToken);
            if (marketData is null)
            {
                continue;
            }

            var findings = new List<QualityFindingDraftDto>();
            foreach (var check in job.Checks.Where(x => x.Enabled).OrderBy(x => x.SortOrder))
            {
                var grain = grainFactory.GetGrain<IValidationCheckGrain>(check.ValidatorId);
                var results = await grain.ValidateAsync(new ValidationCheckMessage
                {
                    JobId = job.Id,
                    ExecutionId = message.ExecutionId,
                    ValidatorId = check.ValidatorId,
                    ValidatorVersion = check.ValidatorVersion,
                    TargetType = target.TargetType,
                    TargetId = target.TargetId,
                    Start = start,
                    End = end,
                    Configuration = check.Configuration
                });
                findings.AddRange(results
                    .Where(x => x.ResultType == "quality.finding")
                    .Select(x => x.Payload.Deserialize<QualityFindingDraftDto>(JsonOptions))
                    .OfType<QualityFindingDraftDto>());
            }

            await qualityRepository.SaveJobEvaluationAsync(
                targets.Count == 1 ? message.ExecutionId : $"{message.ExecutionId}-{targetIndex + 1}",
                job.Id,
                message.TriggerType,
                new ManualQualityEvaluationResult(
                marketData.Metadata,
                start,
                end,
                marketData.Points.Count,
                OverallStatus(findings),
                findings),
                cancellationToken);
        }
    }

    private static async Task<List<QualityValidationJobTargetDto>> ResolveTargets(QualityValidationJobDto job, IQualityRepository qualityRepository, CancellationToken cancellationToken)
    {
        var targets = new List<QualityValidationJobTargetDto>();
        foreach (var target in job.Targets)
        {
            if (target.TargetType == "dataset" || target.TargetType == "series")
            {
                targets.Add(target);
            }
            else if (target.TargetType == "group")
            {
                var members = await qualityRepository.GetSeriesGroupMembersAsync(target.TargetId, cancellationToken);
                targets.AddRange(members.Select(x => new QualityValidationJobTargetDto("series", x.SeriesId, target.Rule)));
            }
        }

        return targets.DistinctBy(x => $"{x.TargetType}:{x.TargetId}", StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string OverallStatus(List<QualityFindingDraftDto> findings)
    {
        if (findings.Any(x => x.QualityStatus == QualityStatuses.Critical))
        {
            return QualityStatuses.Critical;
        }

        return findings.Count == 0 ? QualityStatuses.Healthy : QualityStatuses.Warning;
    }
}
