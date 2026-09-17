using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TimeLens.Domain.Observability;

namespace TimeLens.Infrastructure.Observability;

public static class ObservabilityExtensions
{
    public static void AddTimeLensObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        var configuration = builder.Configuration.GetSection("Observability");
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));
        var enabled = configuration.GetValue("Enabled", true);
        var exportLogs = configuration.GetValue("ExportLogs", false);
        var environment = configuration["Environment"] ?? builder.Environment.EnvironmentName;
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var resource = ResourceBuilder.CreateDefault()
            .AddService(serviceName: serviceName, serviceVersion: version)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment"] = environment,
                ["service.namespace"] = "timelens"
            });

        builder.Logging.Configure(options =>
        {
            options.ActivityTrackingOptions =
                ActivityTrackingOptions.TraceId |
                ActivityTrackingOptions.SpanId |
                ActivityTrackingOptions.ParentId;
        });
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.SetResourceBuilder(resource);
            options.IncludeScopes = true;
            options.ParseStateValues = true;
            if (enabled && exportLogs)
            {
                options.AddOtlpExporter(otlp => ConfigureOtlp(otlp, configuration));
            }
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder.AddService(serviceName, serviceVersion: version)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = environment,
                    ["service.namespace"] = "timelens"
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(Clamp(configuration.GetValue("SamplingRatio", 1.0)))))
                    .AddProcessor(new DropSensitiveTraceAttributesProcessor())
                    .AddSource(TimeLensTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddNpgsql();

                if (enabled)
                {
                    tracing.AddOtlpExporter(otlp => ConfigureOtlp(otlp, configuration));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(TimeLensTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (enabled)
                {
                    metrics.AddOtlpExporter(otlp => ConfigureOtlp(otlp, configuration));
                }
            });
    }

    private static void ConfigureOtlp(OpenTelemetry.Exporter.OtlpExporterOptions options, IConfiguration configuration)
    {
        var endpoint = configuration["OtlpEndpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            options.Endpoint = new Uri(endpoint);
        }
    }

    private static double Clamp(double value) => Math.Min(Math.Max(value, 0), 1);

    private sealed class DropSensitiveTraceAttributesProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            data.SetTag("db.query.text", null);
            data.SetTag("db.statement", null);
        }
    }
}
