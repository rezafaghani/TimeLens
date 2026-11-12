using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Observability;
using TimeLens.Infrastructure;
using TimeLens.Infrastructure.Observability;
using TimeLens.Infrastructure.Repositories;

namespace TimeLens.Insert;

public class IngestionArtifactWorker(
    IServiceScopeFactory scopeFactory,
    IObjectStorage objectStorage,
    IIngestionArtifactPublisher publisher,
    IOptions<RabbitMqOptions> rabbitOptions,
    IOptions<ArtifactWorkerOptions> workerOptions,
    ILogger<IngestionArtifactWorker> logger) : BackgroundService
{
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

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
            catch (Exception exception)
            {
                logger.LogError(exception, "RabbitMQ artifact listener failed. Retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var options = rabbitOptions.Value;
        var factory = new ConnectionFactory
        {
            HostName = options.HostName,
            Port = options.Port,
            VirtualHost = options.VirtualHost,
            UserName = options.UserName,
            Password = options.Password
        };
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.ExchangeDeclareAsync(options.ExchangeName, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(
            options.IngestionArtifactsQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = -1
            },
            cancellationToken: stoppingToken);
        await channel.QueueBindAsync(
            options.IngestionArtifactsQueueName,
            options.ExchangeName,
            options.IngestionArtifactsQueueName,
            cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
        {
            var parent = RabbitMqTraceContext.Extract(eventArgs.BasicProperties);
            using var activity = TimeLensTelemetry.ActivitySource.StartActivity(
                "RabbitMQ Consume IngestionArtifactMessage",
                ActivityKind.Consumer,
                parent.ActivityContext);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", options.IngestionArtifactsQueueName);
            activity?.SetTag("messaging.operation.name", "consume");
            activity?.SetTag("messaging.message.id", eventArgs.BasicProperties.MessageId);

            try
            {
                var json = System.Text.Encoding.UTF8.GetString(eventArgs.Body.ToArray());
                IngestionArtifactMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<IngestionArtifactMessage>(json, _jsonOptions);
                }
                catch (JsonException exception)
                {
                    logger.LogError(exception, "Discarding malformed ingestion artifact message {MessageId}.", eventArgs.BasicProperties.MessageId);
                    await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    return;
                }

                if (message is null || !IsValid(message))
                {
                    logger.LogError("Discarding invalid ingestion artifact message {MessageId}.", eventArgs.BasicProperties.MessageId);
                    await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    return;
                }

                activity?.SetTag("messaging.message.id", message.IngestionId.ToString("D"));
                activity?.SetTag("market.series_id", message.SeriesId);
                await ProcessAsync(message, stoppingToken);
                TimeLensTelemetry.MessagesConsumed.Add(1, KeyValuePair.Create<string, object?>("messaging.destination.name", options.IngestionArtifactsQueueName));
                await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                activity.RecordException(exception);
                TimeLensTelemetry.MessageFailures.Add(1, KeyValuePair.Create<string, object?>("messaging.destination.name", options.IngestionArtifactsQueueName));
                logger.LogError(exception, "Failed to process ingestion artifact message; RabbitMQ will redeliver it.");
                await channel.BasicNackAsync(eventArgs.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(options.IngestionArtifactsQueueName, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Listening for ingestion artifacts on quorum queue {QueueName}.", options.IngestionArtifactsQueueName);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task ProcessAsync(IngestionArtifactMessage message, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var artifactRepository = scope.ServiceProvider.GetRequiredService<IngestionArtifactRepository>();
        var claim = await artifactRepository.TryBeginAsync(message, cancellationToken);
        switch (claim.Disposition)
        {
            case IngestionArtifactDisposition.AlreadyProcessed:
                return;
            case IngestionArtifactDisposition.Duplicate:
                logger.LogInformation(
                    "Marked ingestion {IngestionId} as duplicate of {OriginalIngestionId} for series {SeriesId}.",
                    message.IngestionId,
                    claim.DuplicateOf,
                    message.SeriesId);
                return;
            case IngestionArtifactDisposition.InProgress:
                await DelayBeforeRetryAsync(cancellationToken);
                await publisher.PublishAsync(message, cancellationToken);
                return;
        }

        try
        {
            var parsedJson = await objectStorage.GetAsync(message.ParsedObjectKey, cancellationToken);
            var actualHash = Convert.ToHexString(SHA256.HashData(parsedJson)).ToLowerInvariant();
            if (!string.Equals(actualHash, message.ParsedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Parsed artifact hash mismatch for ingestion {message.IngestionId:D}.");
            }

            var parsed = JsonSerializer.Deserialize<ParsedDatasetArtifact>(parsedJson, _jsonOptions)
                ?? throw new InvalidDataException($"Parsed artifact {message.ParsedObjectKey} is empty.");
            if (parsed.SeriesId != message.SeriesId
                || parsed.Points.Count != message.PointCount)
            {
                throw new InvalidDataException($"Parsed artifact metadata does not match ingestion {message.IngestionId:D}.");
            }

            var timeSeriesRepository = scope.ServiceProvider.GetRequiredService<ITimeSeriesRepository>();
            var result = await timeSeriesRepository.InsertBatchAsync(new TimeSeriesBatchRequest
            {
                SeriesId = parsed.SeriesId,
                SourceMetadataVersion = message.SourceMetadataVersion,
                Points = parsed.Points
            }, cancellationToken);
            await artifactRepository.MarkCompletedAsync(message, result.Inserted, result.Skipped, cancellationToken);
            TimeLensTelemetry.RecordsIngested.Add(result.Inserted);
            logger.LogInformation(
                "Processed ingestion {IngestionId} for {SeriesId}: inserted {Inserted}, skipped {Skipped}.",
                message.IngestionId,
                message.SeriesId,
                result.Inserted,
                result.Skipped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await artifactRepository.MarkFailedAsync(message.IngestionId, exception.Message, CancellationToken.None);
            var retryLimit = Math.Max(workerOptions.Value.MaxRetries, 0);
            if (message.RetryCount >= retryLimit)
            {
                logger.LogError(
                    exception,
                    "Ingestion {IngestionId} failed after {Attempts} attempts and was recorded as failed in PostgreSQL.",
                    message.IngestionId,
                    message.RetryCount + 1);
                return;
            }

            await DelayBeforeRetryAsync(cancellationToken);
            message.RetryCount++;
            await publisher.PublishAsync(message, cancellationToken);
            logger.LogWarning(
                exception,
                "Scheduled retry {RetryCount} of {RetryLimit} for ingestion {IngestionId}.",
                message.RetryCount,
                retryLimit,
                message.IngestionId);
        }
    }

    private async Task DelayBeforeRetryAsync(CancellationToken cancellationToken)
    {
        var delay = Math.Max(workerOptions.Value.RetryDelaySeconds, 1);
        await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
    }

    private static bool IsValid(IngestionArtifactMessage message) =>
        message.IngestionId != Guid.Empty
        && !string.IsNullOrWhiteSpace(message.JobId)
        && !string.IsNullOrWhiteSpace(message.ExecutionId)
        && message.SeriesId > 0
        && !string.IsNullOrWhiteSpace(message.ProviderSeriesId)
        && !string.IsNullOrWhiteSpace(message.RawObjectKey)
        && !string.IsNullOrWhiteSpace(message.ParsedObjectKey)
        && message.ParsedSha256.Length == 64
        && message.ParsedSha256.All(Uri.IsHexDigit)
        && message.PointCount >= 0
        && message.ArtifactCount > 0;
}
