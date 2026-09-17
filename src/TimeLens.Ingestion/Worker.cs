using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TimeLens.Domain.Models;
using TimeLens.Domain.Observability;
using TimeLens.Infrastructure.Observability;
using TimeLens.Ingestion.Grains;
using Microsoft.Extensions.Options;
using Orleans;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace TimeLens.Ingestion;

public class Worker(
    IGrainFactory grainFactory,
    IOptions<RabbitMqOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
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
                logger.LogError(ex, "RabbitMQ ingestion listener failed. Retrying in 5 seconds.");
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
            rabbitOptions.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: stoppingToken);
        await channel.QueueBindAsync(rabbitOptions.QueueName, rabbitOptions.ExchangeName, rabbitOptions.QueueName, cancellationToken: stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, eventArgs) =>
        {
            var parent = RabbitMqTraceContext.Extract(eventArgs.BasicProperties);
            using var activity = TimeLensTelemetry.ActivitySource.StartActivity("RabbitMQ Consume IngestionJobMessage", ActivityKind.Consumer, parent.ActivityContext);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", rabbitOptions.QueueName);
            activity?.SetTag("messaging.operation.name", "consume");
            activity?.SetTag("messaging.message.id", eventArgs.BasicProperties.MessageId);
            try
            {
                var json = Encoding.UTF8.GetString(eventArgs.Body.ToArray());
                var message = JsonSerializer.Deserialize<IngestionJobMessage>(json);
                if (message is null)
                {
                    await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                    return;
                }

                activity?.SetTag("timelens.job.id", message.JobId);
                activity?.SetTag("timelens.execution.id", message.ExecutionId);
                activity?.SetTag("market.provider", message.Source);
                activity?.SetTag("market.series_id", message.SeriesId);
                if (message.Source == "coinbase-exchange")
                {
                    var grain = grainFactory.GetGrain<ICoinbaseCandleGrain>(message.SeriesId);
                    await grain.IngestAsync(json, stoppingToken);
                }
                else if (message.Source == "energy-charts")
                {
                    var grain = grainFactory.GetGrain<IEnergyChartsPriceGrain>(message.SeriesId);
                    await grain.IngestAsync(json, stoppingToken);
                }
                else
                {
                    throw new InvalidOperationException($"Ingestion source '{message.Source}' is not supported.");
                }
                TimeLensTelemetry.MessagesConsumed.Add(1, KeyValuePair.Create<string, object?>("messaging.destination.name", rabbitOptions.QueueName));
                await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
            catch (Exception ex)
            {
                activity.RecordException(ex);
                TimeLensTelemetry.MessageFailures.Add(1, KeyValuePair.Create<string, object?>("messaging.destination.name", rabbitOptions.QueueName));
                logger.LogError(ex, "Failed to process ingestion job message.");
                await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(rabbitOptions.QueueName, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Listening for ingestion jobs on RabbitMQ queue {QueueName}.", rabbitOptions.QueueName);

        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
