namespace TimeLens.Ingestion;

public class IngestionOptions
{
    public int LookbackHours { get; set; } = 48;
    public int BatchSize { get; set; } = 500;
    public int MinRequestIntervalSeconds { get; set; } = 3;
    public int MaxProviderRetries { get; set; } = 3;
    public int RetryBaseDelaySeconds { get; set; } = 10;
}

public class MarketDataRequestDefinition
{
    public string Endpoint { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = [];
}

public class RabbitMqOptions
{
    public string HostName { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string VirtualHost { get; set; } = "timelens";
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = string.Empty;
    public string ExchangeName { get; set; } = "timelens.jobs";
    public string QueueName { get; set; } = "timelens.ingestion.jobs";
}
