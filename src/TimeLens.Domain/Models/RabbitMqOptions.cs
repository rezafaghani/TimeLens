namespace TimeLens.Domain.Models;

public class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "timelens";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = string.Empty;
    public string ExchangeName { get; set; } = "timelens.jobs";
    public string QueueName { get; set; } = "timelens.ingestion.jobs";
    public string ValidationQueueName { get; set; } = "timelens.validation.jobs";
    public string MarketDataUpdatesQueueName { get; set; } = "timelens.market-data.updates";
}
