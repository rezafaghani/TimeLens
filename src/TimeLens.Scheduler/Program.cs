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

            var defaultEnergyScheduleIds = schedules
                .Where(schedule => schedule.Source == "energy-charts")
                .Select(schedule => schedule.Id)
                .ToHashSet(StringComparer.Ordinal);
            var ingestionRepository = scope.ServiceProvider.GetRequiredService<IIngestionControlRepository>();
            var configuredSchedules = await ingestionRepository.GetSchedulesAsync(cancellationToken: CancellationToken.None);
            var qualityRepository = scope.ServiceProvider.GetRequiredService<IQualityRepository>();
            foreach (var schedule in configuredSchedules)
            {
                var validationJob = DefaultQualityValidationJobs.Create(schedule);
                if (await qualityRepository.GetJobAsync(validationJob.Id!, CancellationToken.None) is null)
                {
                    await qualityRepository.UpsertJobAsync(validationJob, CancellationToken.None);
                }
            }

            var publisher = scope.ServiceProvider.GetRequiredService<RabbitMqJobPublisher>();
            foreach (var schedule in configuredSchedules.Where(schedule =>
                         schedule.Enabled && defaultEnergyScheduleIds.Contains(schedule.Id)))
            {
                var biddingZone = schedule.Parameters.GetValueOrDefault("bzn", "DK1").ToLowerInvariant();
                var message = await ingestionRepository.TryCreateBackloadJobAsync(
                    schedule,
                    schedule.Endpoint,
                    schedule.Source,
                    new Dictionary<string, string>(schedule.Parameters),
                    DefaultSchedules.EnergyHistoryStartExpression,
                    "now",
                    schedule.BatchSize,
                    $"energy-history-{biddingZone}-backfill",
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);
                if (message is not null)
                {
                    await publisher.PublishAsync(message, CancellationToken.None);
                }
            }

            return;
        }
        catch when (attempt < 12)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}
