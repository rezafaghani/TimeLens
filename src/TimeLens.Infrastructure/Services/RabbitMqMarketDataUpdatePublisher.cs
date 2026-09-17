using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using RabbitMQ.Client;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Observability;
using TimeLens.Infrastructure.Observability;

namespace TimeLens.Infrastructure.Services;

public class RabbitMqMarketDataUpdatePublisher(TimeLensContext context, IOptions<RabbitMqOptions> options) : IMarketDataUpdatePublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RabbitMqOptions _options = options.Value;

    public async Task PublishAsync(string datasetId, TimeSeriesWritePoint point, DateTimeOffset asOf, string operation, CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(datasetId, cancellationToken);
        if (metadata is null)
        {
            return;
        }

        var message = new MarketDataUpdatedEvent
        {
            DatasetId = metadata.Id,
            SeriesId = metadata.SeriesId,
            ProviderId = metadata.Provider,
            Exchange = metadata.Exchange,
            InstrumentId = metadata.ProviderInstrumentId,
            Symbol = metadata.Symbol,
            MarketDataType = metadata.MarketDataType,
            Timeframe = metadata.Timeframe,
            Operation = operation,
            DataTimestamp = point.Timestamp,
            AsOf = asOf,
            Bar = point
        };

        using var activity = TimeLensTelemetry.ActivitySource.StartActivity("MarketDataUpdatePublication", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", _options.MarketDataUpdatesQueueName);
        activity?.SetTag("messaging.operation.name", "publish");
        activity?.SetTag("market.provider", message.ProviderId);
        activity?.SetTag("market.symbol", message.Symbol);
        activity?.SetTag("market.timeframe", message.Timeframe);
        activity?.SetTag("market.data_timestamp", message.DataTimestamp.ToUnixTimeMilliseconds());
        activity?.SetTag("messaging.message_id", message.EventId);

        var factory = new ConnectionFactory
        {
            HostName = _options.HostName,
            Port = _options.Port,
            VirtualHost = _options.VirtualHost,
            UserName = _options.UserName,
            Password = _options.Password
        };
        await using var connection = await factory.CreateConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(_options.ExchangeName, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            _options.MarketDataUpdatesQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(_options.MarketDataUpdatesQueueName, _options.ExchangeName, _options.MarketDataUpdatesQueueName, cancellationToken: cancellationToken);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.EventId,
            Type = nameof(MarketDataUpdatedEvent),
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        };
        RabbitMqTraceContext.Inject(properties);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, JsonOptions));
        await channel.BasicPublishAsync(_options.ExchangeName, _options.MarketDataUpdatesQueueName, true, properties, body, cancellationToken);
        TimeLensTelemetry.MessagesPublished.Add(1, KeyValuePair.Create<string, object?>("messaging.destination.name", _options.MarketDataUpdatesQueueName));
    }

    private async Task<DatasetMetadataDto?> GetMetadataAsync(string datasetId, CancellationToken cancellationToken)
    {
        await using var command = context.Postgres.CreateCommand("""
            SELECT id, series_id, provider, exchange, symbol, market_data_type, timeframe, provider_instrument_id
            FROM market_data_series
            WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", datasetId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DatasetMetadataDto
            {
                Id = reader.GetString(0),
                SeriesId = reader.GetString(1),
                Provider = reader.GetString(2),
                Exchange = reader.GetString(3),
                Symbol = reader.GetString(4),
                MarketDataType = reader.GetString(5),
                Timeframe = reader.GetString(6),
                ProviderInstrumentId = reader.GetString(7)
            }
            : null;
    }
}
