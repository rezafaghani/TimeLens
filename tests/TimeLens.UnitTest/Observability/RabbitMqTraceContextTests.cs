using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;
using TimeLens.Domain.Observability;
using TimeLens.Infrastructure.Observability;

namespace TimeLens.UnitTest.Observability;

public class RabbitMqTraceContextTests
{
    [Fact]
    public void InjectAndExtract_RabbitMqHeaders_PreservesTraceContext()
    {
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TimeLensTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = TimeLensTelemetry.ActivitySource.StartActivity("test");
        Assert.NotNull(activity);

        var properties = new BasicProperties();
        RabbitMqTraceContext.Inject(properties);

        var extracted = RabbitMqTraceContext.Extract(properties);

        Assert.Equal(activity.TraceId, extracted.ActivityContext.TraceId);
        Assert.Equal(activity.SpanId, extracted.ActivityContext.SpanId);
        Assert.DoesNotContain(properties.Headers!, header => header.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase));
    }
}
