using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;
using TimeLens.Infrastructure;
using TimeLens.Infrastructure.Repositories;
using TimeLens.Validation;
using TimeLens.Validation.Worker;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddSingleton<TimeLensContext>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is not configured.");
    var clickHouse = configuration.GetConnectionString("ClickHouse") ?? throw new InvalidOperationException("ClickHouse connection string is not configured.");
    return new TimeLensContext(postgres, clickHouse);
});
builder.Services.AddScoped<IDatasetRepository, DatasetRepository>();
builder.Services.AddScoped<ITimeSeriesRepository, TimeSeriesRepository>();
builder.Services.AddScoped<IQualityRepository, QualityRepository>();
builder.Services.AddScoped<ValidationExecutionService>();
foreach (var pluginType in typeof(QualityValidationEngine).Assembly.GetTypes()
    .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(IExecutionPlugin).IsAssignableFrom(type)))
{
    builder.Services.AddScoped(typeof(IExecutionPlugin), pluginType);
}
builder.Host.UseOrleans(siloBuilder =>
{
    siloBuilder.UseLocalhostClustering();
});
builder.Services.AddHostedService<ValidationJobConsumer>();

var app = builder.Build();
await InitializeAsync(app.Services);
await RegisterValidationPluginsAsync(app.Services);

app.MapGet("/validation/plugins", (IEnumerable<IExecutionPlugin> plugins) =>
    plugins.Select(x => x.Metadata).OrderBy(x => x.Id));

app.MapPost("/validation/evaluate", async (
    ManualQualityEvaluationRequest request,
    ValidationExecutionService validation,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.DatasetId)
        || !DateTimeExpression.TryResolve(request.Start, out var start, request.TimeZone)
        || !DateTimeExpression.TryResolve(request.End, out var end, request.TimeZone)
        || start >= end)
    {
        return Results.BadRequest("A valid dataset id and half-open date range are required.");
    }

    DateTimeOffset? asOf = null;
    if (!string.IsNullOrWhiteSpace(request.AsOf))
    {
        if (!DateTimeExpression.TryResolve(request.AsOf, out var parsedAsOf, request.TimeZone))
        {
            return Results.BadRequest("A valid asOf version time is required.");
        }

        asOf = parsedAsOf;
    }

    try
    {
        var result = await validation.EvaluateDatasetAsync(request.DatasetId, start, end, asOf, request.TimeZone, null, cancellationToken, request);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.MapPost("/validation/jobs/{id}/runs", async (
    string id,
    RunQualityJobRequest request,
    IQualityRepository qualityRepository,
    ValidationExecutionService validation,
    CancellationToken cancellationToken) =>
{
    var job = await qualityRepository.GetJobAsync(id, cancellationToken);
    if (job is null)
    {
        return Results.NotFound();
    }

    if (!DateTimeExpression.TryResolve(request.Start ?? job.WindowStartExpression, out var start, job.TimeZone)
        || !DateTimeExpression.TryResolve(request.End ?? job.WindowEndExpression, out var end, job.TimeZone)
        || start >= end)
    {
        return Results.BadRequest("A valid half-open evaluation window is required.");
    }

    var validatorIds = job.Checks.Where(x => x.Enabled).Select(x => x.ValidatorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
    if (validatorIds.Count == 0)
    {
        return Results.BadRequest("The quality job has no enabled validation checks.");
    }

    try
    {
        var datasetIds = await ResolveTargetDatasetIds(job, qualityRepository, cancellationToken);
        var results = new List<ManualQualityEvaluationResult>();
        foreach (var datasetId in datasetIds)
        {
            var result = await validation.EvaluateDatasetAsync(datasetId, start, end, null, job.TimeZone, validatorIds, cancellationToken);
            if (result is not null)
            {
                results.Add(result);
            }
        }

        return Results.Ok(new RunQualityJobResult(
            job.Id,
            string.IsNullOrWhiteSpace(request.TriggerType) ? "manual" : request.TriggerType,
            datasetIds.Count,
            results.Count,
            results.Sum(x => x.Findings.Count),
            results.Sum(x => x.Findings.Count(finding => finding.Severity == "critical")),
            results));
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(exception.Message);
    }
});

app.Run();

static async Task<List<string>> ResolveTargetDatasetIds(QualityValidationJobDto job, IQualityRepository qualityRepository, CancellationToken cancellationToken)
{
    var ids = new List<string>();
    foreach (var target in job.Targets)
    {
        if (target.TargetType == "dataset")
        {
            ids.Add(target.TargetId);
        }
        else if (target.TargetType == "group")
        {
            var members = await qualityRepository.GetSeriesGroupMembersAsync(target.TargetId, cancellationToken);
            ids.AddRange(members.Select(x => x.DatasetId));
        }
    }

    return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

static async Task InitializeAsync(IServiceProvider services)
{
    for (var attempt = 1; attempt <= 12; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TimeLensContext>().EnsurePostgresSchemaAsync();
            return;
        }
        catch when (attempt < 12)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}

static async Task RegisterValidationPluginsAsync(IServiceProvider services)
{
    using var scope = services.CreateScope();
    var plugins = scope.ServiceProvider.GetServices<IExecutionPlugin>()
        .OfType<QualityValidationPlugin>()
        .Select(plugin => new RegisteredValidationPluginDto(
            plugin.ValidatorId,
            plugin.Category,
            plugin.Metadata.Name,
            plugin.Metadata.Description,
            "series",
            plugin.Metadata.Version,
            plugin.DefaultSeverity,
            plugin.Metadata.ConfigurationSchema,
            ValidationPluginUsage.Api | ValidationPluginUsage.Scheduler | ValidationPluginUsage.Worker,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow))
        .ToList();
    await scope.ServiceProvider.GetRequiredService<IQualityRepository>().UpsertValidatorTypesAsync(plugins);
}
