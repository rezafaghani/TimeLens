using Scalar.AspNetCore;
using TimeLens.API.Hubs;
using TimeLens.Infrastructure.Observability;
var  myAllowSpecificOrigins = "_myAllowSpecificOrigins";
var builder = WebApplication.CreateBuilder(args);

builder.AddTimeLensObservability("timelens-api");
// Add services to the container.


builder.Services.AddOpenApi();

builder.AddApplicationServices();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddExceptionHandler<BadRequestExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: myAllowSpecificOrigins,
        policy  =>
        {
            policy.WithOrigins("http://127.0.0.1:5173", "http://localhost:5173")
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials();
        });
});
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();
var app = builder.Build();

// Perform database initialization
await DatabaseInitializer.InitializeAsync(app.Services);

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors(myAllowSpecificOrigins);
app.UseExceptionHandler();
app.UseAuthorization();

app.MapControllers();
app.MapHub<MarketDataHub>("/hubs/market-data");
app.MapHealthChecks("/health");



app.Run();
