using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Infrastructure.Observability;

namespace TimeLens.Infrastructure.Services;

public class RabbitMqIngestionArtifactPublisher(IOptions<RabbitMqOptions> options) : IIngestionArtifactPublisher
{
    private readonly RabbitMqOptions _options = options.Value;

    public async Task PublishAsync(IngestionArtifactMessage message, CancellationToken cancellationToken = default)
    {
        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password
        };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await channel.ExchangeDeclareAsync(_options.ExchangeName, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            _options.IngestionArtifactsQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = -1
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            _options.IngestionArtifactsQueueName,
            _options.ExchangeName,
            _options.IngestionArtifactsQueueName,
            cancellationToken: cancellationToken);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = message.IngestionId.ToString("D"),
            Type = nameof(IngestionArtifactMessage)
        };
        RabbitMqTraceContext.Inject(properties);

        await channel.BasicPublishAsync(
            exchange: _options.ExchangeName,
            routingKey: _options.IngestionArtifactsQueueName,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }
}
