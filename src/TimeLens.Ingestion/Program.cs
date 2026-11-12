using TimeLens.Ingestion;
using TimeLens.Ingestion.Services;
using TimeLens.Domain.Models;
using TimeLens.Domain.Interfaces;
using TimeLens.Infrastructure;
using TimeLens.Infrastructure.Repositories;
using TimeLens.Infrastructure.Observability;
using TimeLens.Infrastructure.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.AddTimeLensObservability("timelens-ingestion");

builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddSingleton<TimeLensContext>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is not configured.");
    return new TimeLensContext(postgres, null);
});
builder.Services.AddScoped<IIngestionControlRepository, IngestionControlRepository>();
builder.Services.AddScoped<IDatasetRepository, DatasetRepository>();
builder.Services.AddSingleton<IngestionArtifactWriter>();
builder.Services.AddSingleton<IIngestionArtifactPublisher, RabbitMqIngestionArtifactPublisher>();
builder.Services.AddTimeLensObjectStorage(builder.Configuration);
builder.Services.AddHttpClient<CoinbaseCandleClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Coinbase:BaseUrl"] ?? "https://api.exchange.coinbase.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeLens/1.0");
});
builder.Services.AddHttpClient<EnergyChartsPriceClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["EnergyCharts:BaseUrl"] ?? "https://api.energy-charts.info/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeLens/1.0");
});
builder.Services.AddHttpClient<CoinMetricsClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["CoinMetrics:BaseUrl"] ?? "https://community-api.coinmetrics.io/v4/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeLens/1.0");
});
builder.Services.AddSingleton<ProviderRateLimiter>();
builder.Services.AddSingleton<CoinbaseCandleNormalizer>();
builder.Services.AddSingleton<EnergyChartsPriceNormalizer>();
builder.Services.AddSingleton<CoinMetricsNormalizer>();
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
});
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

await InitializeAsync(host.Services);

host.Run();

static async Task InitializeAsync(IServiceProvider services)
{
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TimeLensContext>().EnsurePostgresSchemaAsync();
            await scope.ServiceProvider.GetRequiredService<IObjectStorage>().EnsureBucketAsync();
            return;
        }
        catch when (attempt < 12)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}
