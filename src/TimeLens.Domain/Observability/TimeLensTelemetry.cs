using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TimeLens.Domain.Observability;

public static class TimeLensTelemetry
{
    public const string ActivitySourceName = "TimeLens";
    public const string MeterName = "TimeLens";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> IngestionExecutions = Meter.CreateCounter<long>("timelens.ingestion.executions.total");
    public static readonly Counter<long> IngestionFailures = Meter.CreateCounter<long>("timelens.ingestion.executions.failed");
    public static readonly Counter<long> RecordsIngested = Meter.CreateCounter<long>("timelens.ingestion.records");
    public static readonly Histogram<double> IngestionDuration = Meter.CreateHistogram<double>("timelens.ingestion.execution.duration", "s");
    public static readonly Histogram<double> ProviderRequestDuration = Meter.CreateHistogram<double>("timelens.provider.request.duration", "s");

    public static readonly Counter<long> ValidationExecutions = Meter.CreateCounter<long>("timelens.validation.executions.total");
    public static readonly Counter<long> ValidationFailures = Meter.CreateCounter<long>("timelens.validation.executions.failed");
    public static readonly Counter<long> ValidationFindings = Meter.CreateCounter<long>("timelens.validation.findings");
    public static readonly Histogram<double> ValidationDuration = Meter.CreateHistogram<double>("timelens.validation.execution.duration", "s");
    public static readonly Histogram<double> ValidatorDuration = Meter.CreateHistogram<double>("timelens.validation.validator.duration", "s");

    public static readonly Counter<long> MessagesPublished = Meter.CreateCounter<long>("timelens.messaging.messages.published");
    public static readonly Counter<long> MessagesConsumed = Meter.CreateCounter<long>("timelens.messaging.messages.consumed");
    public static readonly Counter<long> MessageFailures = Meter.CreateCounter<long>("timelens.messaging.processing.failures");

    public static readonly Counter<long> LiveUpdatesDispatched = Meter.CreateCounter<long>("timelens.realtime.updates.dispatched");

    public static void RecordException(this Activity? activity, Exception exception)
    {
        activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity?.AddException(exception);
    }
}
