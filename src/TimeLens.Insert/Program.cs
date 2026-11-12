using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Infrastructure;
using TimeLens.Infrastructure.Observability;
using TimeLens.Infrastructure.Repositories;
using TimeLens.Infrastructure.Services;
using TimeLens.Insert;

var builder = Host.CreateApplicationBuilder(args);
builder.AddTimeLensObservability("timelens-insert");

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.Configure<ArtifactWorkerOptions>(builder.Configuration.GetSection("ArtifactWorker"));
builder.Services.AddSingleton<TimeLensContext>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is not configured.");
    var clickHouse = configuration.GetConnectionString("ClickHouse") ?? throw new InvalidOperationException("ClickHouse connection string is not configured.");
    return new TimeLensContext(postgres, clickHouse);
});
builder.Services.AddTimeLensObjectStorage(builder.Configuration);
builder.Services.AddSingleton<IMarketDataUpdatePublisher, RabbitMqMarketDataUpdatePublisher>();
builder.Services.AddSingleton<IIngestionArtifactPublisher, RabbitMqIngestionArtifactPublisher>();
builder.Services.AddScoped<ITimeSeriesRepository, TimeSeriesRepository>();
builder.Services.AddScoped<IngestionArtifactRepository>();
builder.Services.AddHostedService<IngestionArtifactWorker>();

var host = builder.Build();
await InitializeAsync(host.Services);
await host.RunAsync();

static async Task InitializeAsync(IServiceProvider services)
{
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TimeLensContext>();
            await context.EnsurePostgresSchemaAsync();
            await context.EnsureClickHouseSchemaAsync();
            await scope.ServiceProvider.GetRequiredService<IObjectStorage>().EnsureBucketAsync();
            return;
        }
        catch when (attempt < 12)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    throw new InvalidOperationException("Insert worker dependencies did not become ready.");
}
