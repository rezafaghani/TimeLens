using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Insert.Services;
using TimeLens.Infrastructure;
using TimeLens.Infrastructure.Repositories;
using TimeLens.Infrastructure.Services;
using TimeLens.Infrastructure.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddTimeLensObservability("timelens-insert");
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
    options.ListenAnyIP(8081, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Services.AddControllers();
builder.Services.AddGrpc();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
builder.Services.AddSingleton<TimeLensContext>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var postgres = configuration.GetConnectionString("Postgres") ?? throw new InvalidOperationException("Postgres connection string is not configured.");
    var clickHouse = configuration.GetConnectionString("ClickHouse") ?? throw new InvalidOperationException("ClickHouse connection string is not configured.");
    return new TimeLensContext(postgres, clickHouse);
});
builder.Services.AddSingleton<IMarketDataUpdatePublisher, RabbitMqMarketDataUpdatePublisher>();
builder.Services.AddScoped<ITimeSeriesRepository, TimeSeriesRepository>();

var authEnabled = builder.Configuration.GetValue("Auth:Enabled", false);
if (authEnabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = builder.Configuration["Auth:Authority"];
            options.Audience = builder.Configuration["Auth:Audience"];
            options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
        });
    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("IngestionWrite", policy =>
        {
            var scope = builder.Configuration["Auth:WriteScope"] ?? "timelens.insert.write";
            policy.RequireAuthenticatedUser();
            policy.RequireAssertion(context =>
                context.User.Claims.Any(claim =>
                    claim.Type is "scope" or "scp" &&
                    claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope)));
        });
    });
}
else
{
    builder.Services.AddAuthorization();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<TimeLensContext>().EnsureClickHouseSchemaAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

if (authEnabled)
{
    app.UseAuthentication();
}

app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");
var grpc = app.MapGrpcService<IngestionWriteGrpcService>();
if (authEnabled)
{
    grpc.RequireAuthorization("IngestionWrite");
}

app.Run();
