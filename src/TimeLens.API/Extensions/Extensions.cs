using TimeLens.API.Infrastructure.Services;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;
using TimeLens.Infrastructure.Repositories;
using TimeLens.Infrastructure.Services;
using TimeLens.Validation;

namespace TimeLens.API.Extensions;

internal static class Extensions
{
    public static void AddApplicationServices(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
        builder.Services.AddSingleton<TimeLensContext>(sp =>
        {
            var configuration = sp.GetService<IConfiguration>();
            if (configuration == null)
            {
                throw new InvalidOperationException("Configuration is not available.");
            }
            var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is not configured.");
            var clickHouse = configuration.GetConnectionString("ClickHouse") ?? throw new InvalidOperationException("ClickHouse connection string is not configured.");
            return new TimeLensContext(postgres, clickHouse);
        });
        services.AddScoped<IDatasetRepository, DatasetRepository>();
        services.AddScoped<ITimeSeriesRepository, TimeSeriesRepository>();
        services.AddScoped<IIngestionControlRepository, IngestionControlRepository>();
        services.AddScoped<IExecutionRepository, ExecutionRepository>();
        services.AddScoped<IQualityRepository, QualityRepository>();
        foreach (var pluginType in typeof(QualityValidationEngine).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(IExecutionPlugin).IsAssignableFrom(type)))
        {
            services.AddScoped(typeof(IExecutionPlugin), pluginType);
        }
        services.AddScoped<ExecutionPluginRegistry>();
        services.AddScoped<ExecutionRuntime>();
        services.AddSingleton<RabbitMqJobPublisher>();
        services.AddSingleton<IValidationJobPublisher>(sp => sp.GetRequiredService<RabbitMqJobPublisher>());
        services.AddHostedService<MarketDataUpdateConsumer>();
        services.AddMemoryCache();
    }
}
