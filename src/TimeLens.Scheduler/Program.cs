using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Infrastructure;
using TimeLens.Infrastructure.Repositories;
using TimeLens.Infrastructure.Services;
using TimeLens.Infrastructure.Observability;
using TimeLens.Scheduler;

var builder = Host.CreateApplicationBuilder(args);
builder.AddTimeLensObservability("timelens-scheduler");

builder.Services.Configure<SchedulerOptions>(builder.Configuration.GetSection("Scheduler"));
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddSingleton<TimeLensContext>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is not configured.");
    return new TimeLensContext(postgres, null);
});
builder.Services.AddScoped<IIngestionControlRepository, IngestionControlRepository>();
builder.Services.AddScoped<IDatasetRepository, DatasetRepository>();
builder.Services.AddScoped<IExecutionRepository, ExecutionRepository>();
builder.Services.AddScoped<IQualityRepository, QualityRepository>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<RabbitMqJobPublisher>();
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
            var schedules = DefaultSchedules.Create();
            var datasets = scope.ServiceProvider.GetRequiredService<IDatasetRepository>();
            foreach (var metadata in DefaultDatasetMetadata.Create(schedules))
            {
                await datasets.UpsertAsync(metadata, CancellationToken.None);
            }

            await scope.ServiceProvider
                .GetRequiredService<IIngestionControlRepository>()
                .EnsureDefaultSchedulesAsync(schedules, CancellationToken.None);
            return;
        }
        catch when (attempt < 12)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}
