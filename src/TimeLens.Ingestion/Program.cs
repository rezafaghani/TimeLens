using TimeLens.Ingestion;
using TimeLens.Ingestion.Services;
using TimeLens.Contracts;
using TimeLens.Domain.Interfaces;
using TimeLens.Infrastructure;
using TimeLens.Infrastructure.Repositories;
using Grpc.Net.Client;

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddHttpClient<CoinbaseCandleClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Coinbase:BaseUrl"] ?? "https://api.exchange.coinbase.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TimeLens/1.0");
});
builder.Services.AddSingleton<ProviderRateLimiter>();
builder.Services.AddHttpClient<OAuthTokenProvider>();
builder.Services.AddSingleton<CoinbaseCandleNormalizer>();
builder.Services.AddScoped<IngestionWriteClient>();
var grpcClientBuilder = builder.Services.AddGrpcClient<IngestionWrite.IngestionWriteClient>(options =>
{
    options.Address = new Uri(builder.Configuration["InsertApi:GrpcUrl"] ?? "http://localhost:5103");
})
.ConfigureChannel(options =>
{
    var grpcUrl = builder.Configuration["InsertApi:GrpcUrl"] ?? "http://localhost:5103";
    options.UnsafeUseInsecureChannelCallCredentials = grpcUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
});

if (!string.IsNullOrWhiteSpace(builder.Configuration["InsertApi:TokenEndpoint"]))
{
    grpcClientBuilder.AddCallCredentials(async (context, metadata, serviceProvider) =>
    {
        var tokenProvider = serviceProvider.GetRequiredService<OAuthTokenProvider>();
        var token = await tokenProvider.GetTokenAsync(context.CancellationToken);
        if (!string.IsNullOrWhiteSpace(token))
        {
            metadata.Add("Authorization", $"Bearer {token}");
        }
    });
}
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
            return;
        }
        catch when (attempt < 12)
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}
