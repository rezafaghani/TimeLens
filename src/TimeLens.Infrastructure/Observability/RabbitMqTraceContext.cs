using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

namespace TimeLens.Infrastructure.Observability;

public static class RabbitMqTraceContext
{
    public static void Inject(IBasicProperties properties)
    {
        properties.Headers ??= new Dictionary<string, object?>();
        Propagators.DefaultTextMapPropagator.Inject(new PropagationContext(Activity.Current?.Context ?? default, Baggage.Current), properties.Headers, static (headers, key, value) => headers[key] = value);
    }

    public static PropagationContext Extract(IReadOnlyBasicProperties properties)
    {
        return Propagators.DefaultTextMapPropagator.Extract(default, properties.Headers, static (headers, key) =>
        {
            if (headers is null || !headers.TryGetValue(key, out var value) || value is null)
            {
                return [];
            }

            return value switch
            {
                byte[] bytes => [Encoding.UTF8.GetString(bytes)],
                string text => [text],
                _ => [value.ToString() ?? string.Empty]
            };
        });
    }
}
