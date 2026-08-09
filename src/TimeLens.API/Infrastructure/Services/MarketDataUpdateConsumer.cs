using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using TimeLens.API.Hubs;
using TimeLens.Domain.Models;

namespace TimeLens.API.Infrastructure.Services;

public class MarketDataUpdateConsumer(
    IHubContext<MarketDataHub> hub,
    IOptions<RabbitMqOptions> options,
    ILogger<MarketDataUpdateConsumer> logger) : BackgroundService
{
    private static readonly ActivitySource ActivitySource = new("TimeLens.MarketData.Live");
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
                logger.LogError(ex, "RabbitMQ market-data update listener failed. Retrying in 5 seconds.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        var rabbit = options.Value;
        var factory = new ConnectionFactory
        {
            HostName = rabbit.HostName,
            Port = rabbit.Port,
            VirtualHost = rabbit.VirtualHost,
            UserName = rabbit.UserName,
            Password = rabbit.Password
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.ExchangeDeclareAsync(rabbit.ExchangeName, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync(
            rabbit.MarketDataUpdatesQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: stoppingToken);
        await channel.QueueBindAsync(rabbit.MarketDataUpdatesQueueName, rabbit.ExchangeName, rabbit.MarketDataUpdatesQueueName, cancellationToken: stoppingToken);
        await channel.BasicQosAsync(0, 50, false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                var json = Encoding.UTF8.GetString(args.Body.ToArray());
                var message = JsonSerializer.Deserialize<MarketDataUpdatedEvent>(json, JsonOptions);
                if (message is not null)
                {
                    await DispatchAsync(message, stoppingToken);
                }

                await channel.BasicAckAsync(args.DeliveryTag, false, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to dispatch market-data update.");
                await channel.BasicAckAsync(args.DeliveryTag, false, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(rabbit.MarketDataUpdatesQueueName, false, consumer, stoppingToken);
        logger.LogInformation("Listening for market-data updates on RabbitMQ queue {QueueName}.", rabbit.MarketDataUpdatesQueueName);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    private async Task DispatchAsync(MarketDataUpdatedEvent message, CancellationToken cancellationToken)
    {
        var group = MarketDataLiveGroups.For(message);
        using var activity = ActivitySource.StartActivity("market_data.live.dispatch");
        activity?.SetTag("market.subscription_group", group);
        activity?.SetTag("market.provider", message.ProviderId);
        activity?.SetTag("market.symbol", message.Symbol);
        activity?.SetTag("market.timeframe", message.Timeframe);
        activity?.SetTag("market.data_timestamp", message.DataTimestamp.ToUnixTimeMilliseconds());
        activity?.SetTag("messaging.message_id", message.EventId);

        await hub.Clients.Group(group).SendAsync("MarketDataUpdated", message, cancellationToken);
        logger.LogInformation(
            "Dispatched market-data update {EventId} to {Group} for {Provider} {Symbol} {Timeframe}.",
            message.EventId,
            group,
            message.ProviderId,
            message.Symbol,
            message.Timeframe);
    }
}
