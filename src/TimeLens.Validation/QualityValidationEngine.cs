using System.Text.Json;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;

namespace TimeLens.Validation;

public static class QualityValidationEngine
{
    public static List<QualityFindingDraftDto> Evaluate(QualityEvaluationRequest request)
    {
        Validate(request);
        var context = new ExecutionPluginContext(
            "dataset",
            request.Metadata.Id,
            request.Start,
            request.End,
            JsonSerializer.SerializeToElement(new
            {
                request.AllowedDelay,
                request.MinimumValue,
                request.MaximumValue,
                request.MaximumAbsoluteChange,
                request.MaximumPercentageChange,
                request.NearZeroFloor,
                request.FlatLinePointCount
            }, JsonOptions),
            request.Metadata,
            request.Points);

        return ValidationPlugins.All
            .SelectMany(plugin => plugin.Evaluate(context, request))
            .ToList();
    }

    public static List<DateTimeOffset> ExpectedTimestamps(DateTimeOffset start, DateTimeOffset end, TimeSpan granularity)
    {
        return ExpectedTimestamps(start, end, granularity, Crypto24x7MarketCalendar.Instance);
    }

    public static List<DateTimeOffset> ExpectedTimestamps(DateTimeOffset start, DateTimeOffset end, TimeSpan granularity, IMarketCalendar calendar)
    {
        if (start >= end || granularity <= TimeSpan.Zero)
        {
            return [];
        }

        var result = new List<DateTimeOffset>();
        for (var timestamp = start; timestamp < end; timestamp = timestamp.Add(granularity))
        {
            if (calendar.IsExpected(timestamp, timestamp.Add(granularity)))
            {
                result.Add(timestamp);
            }
        }

        return result;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static void Validate(QualityEvaluationRequest request)
    {
        if (request.Start >= request.End)
        {
            throw new ArgumentException("Quality evaluation requires a half-open range where start is before end.");
        }

        if (request.Granularity <= TimeSpan.Zero)
        {
            throw new ArgumentException("Quality evaluation requires a positive granularity.");
        }
    }

    internal static QualityFindingDraftDto Finding(
        string validatorId,
        string category,
        string severity,
        string status,
        string title,
        string message,
        DateTimeOffset? affectedStart,
        DateTimeOffset? affectedEnd,
        int? expectedCount = null,
        int? actualCount = null,
        int? affectedCount = null,
        IEnumerable<DateTimeOffset>? samples = null,
        JsonElement? details = null) =>
        new(validatorId, category, severity, status, title, message, affectedStart, affectedEnd, expectedCount, actualCount, affectedCount, samples?.ToList() ?? [], details);

    internal static QualityFindingDraftDto InsufficientData(string validatorId, string title, string message, QualityEvaluationRequest request, int actualCount) =>
        Finding(validatorId, "anomaly", "informational", QualityStatuses.InsufficientData,
            title, message, request.Start, request.End, actualCount: actualCount);

    internal static string ToIsoDuration(string timeframe) => timeframe switch
    {
        "1m" => "PT1M",
        "5m" => "PT5M",
        "15m" => "PT15M",
        "1h" => "PT1H",
        "1d" => "P1D",
        _ => timeframe
    };
}

public interface IMarketCalendar
{
    string Id { get; }
    bool IsExpected(DateTimeOffset start, DateTimeOffset end);
}

public sealed class Crypto24x7MarketCalendar : IMarketCalendar
{
    public static readonly Crypto24x7MarketCalendar Instance = new();
    public string Id => "crypto-24x7";
    public bool IsExpected(DateTimeOffset start, DateTimeOffset end) => start < end;
}

internal static class MarketCalendars
{
    public static IMarketCalendar Resolve(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "" or null or "crypto" or "crypto-24x7" or "24x7" => Crypto24x7MarketCalendar.Instance,
        _ => Crypto24x7MarketCalendar.Instance
    };
}

internal static class ValidationConfig
{
    public static bool? GetOptionalBool(JsonElement configuration, string name)
    {
        return configuration.ValueKind == JsonValueKind.Object
            && configuration.TryGetProperty(name, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : null;
    }
}

public abstract class QualityValidationPlugin(
    string validatorId,
    string category,
    string name,
    string description,
    string defaultSeverity,
    object defaultConfiguration) : IExecutionPlugin
{
    public const string IdPrefix = "timelens.validation.";
    public string ValidatorId { get; } = validatorId;
    public string Category { get; } = category;
    public string DefaultSeverity { get; } = defaultSeverity;

    public ExecutionPluginDto Metadata { get; } = new(
        IdPrefix + validatorId,
        name,
        description,
        ExecutionCategories.Validation,
        1,
        ["dataset", "series"],
        [],
        [],
        JsonSerializer.SerializeToElement(defaultConfiguration, QualityValidationEngine.JsonOptions),
        JsonSerializer.SerializeToElement(defaultConfiguration, QualityValidationEngine.JsonOptions),
        "quality.finding");

    public Task<List<ExecutionStepResultDto>> ExecuteAsync(ExecutionPluginContext context, CancellationToken cancellationToken = default)
    {
        if (context.Dataset is null)
        {
            throw new ArgumentException($"Dataset '{context.TargetId}' was not found.");
        }

        if (!ExecutionPluginConfiguration.TryGetDuration(context.Configuration, "granularity", out var granularity)
            && !ExecutionPluginConfiguration.TryParseDuration(QualityValidationEngine.ToIsoDuration(context.Dataset.Timeframe), out granularity))
        {
            throw new ArgumentException($"Dataset '{context.TargetId}' has invalid granularity.");
        }

        var request = new QualityEvaluationRequest(
            context.Dataset,
            context.Start,
            context.End,
            DateTimeOffset.UtcNow,
            granularity,
            ExecutionPluginConfiguration.GetOptionalDuration(context.Configuration, "allowedDelay"),
            ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "minimumValue", "min"),
            ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "maximumValue", "max"),
            ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "maximumAbsoluteChange", "maxAbsoluteChange"),
            ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "maximumPercentageChange", "maxPercentageChange"),
            ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "nearZeroFloor") ?? 0.000001,
            ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "flatLinePointCount") ?? 0,
            context.Points);
        QualityValidationEngine.Validate(request);

        var results = Evaluate(context, request)
            .Select(finding => new ExecutionStepResultDto(
                Metadata.Id,
                context.TargetId,
                finding.Title == "Provider missing data" ? ExecutionRunStatuses.ProviderMissingData : finding.QualityStatus,
                Metadata.ResultType,
                finding.Title,
                JsonSerializer.SerializeToElement(new
                {
                    finding.ExpectedCount,
                    finding.ActualCount,
                    finding.AffectedCount
                }, QualityValidationEngine.JsonOptions),
                JsonSerializer.SerializeToElement(finding, QualityValidationEngine.JsonOptions)))
            .ToList();
        return Task.FromResult(results);
    }

    internal abstract List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request);

    protected static List<TimeSeriesPointDto> Points(QualityEvaluationRequest request) =>
        request.Points.OrderBy(x => x.Timestamp).ToList();
}

public sealed class RequiredMetadataValidationPlugin() : QualityValidationPlugin(
    "metadata.required",
    "metadata",
    "Required metadata",
    "Checks required market-data metadata such as provider, symbol, data type, timeframe, unit, and time zone.",
    "warning",
    new { requiredFields = new[] { "provider", "symbol", "marketDataType", "timeframe", "unit" } })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Metadata.Provider)) missing.Add("provider");
        if (string.IsNullOrWhiteSpace(request.Metadata.Symbol)) missing.Add("symbol");
        if (string.IsNullOrWhiteSpace(request.Metadata.MarketDataType)) missing.Add("marketDataType");
        if (string.IsNullOrWhiteSpace(request.Metadata.Timeframe)) missing.Add("timeframe");
        if (string.IsNullOrWhiteSpace(request.Metadata.Unit)) missing.Add("unit");

        return missing.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("metadata.required", "metadata", "warning", QualityStatuses.Degraded,
                "Required metadata is missing",
                $"Missing metadata: {string.Join(", ", missing)}.",
                request.Start, request.End, affectedCount: missing.Count)
        ];
    }
}

public sealed class EmptyDataValidationPlugin() : QualityValidationPlugin(
    "availability.empty-data",
    "availability",
    "Empty data",
    "Checks whether the requested evaluation window returns any usable data.",
    "critical",
    new { })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var points = Points(request);
        var expected = QualityValidationEngine.ExpectedTimestamps(request.Start, request.End, request.Granularity, MarketCalendars.Resolve(request.Metadata.Calendar));
        if (expected.Count == 0)
        {
            return
            [
                QualityValidationEngine.Finding("availability.empty-data", "availability", "informational", QualityStatuses.MarketClosed,
                    "Market closed",
                    "No observations were expected in this evaluation range for the configured market calendar.",
                    request.Start, request.End, expectedCount: 0, actualCount: points.Count)
            ];
        }

        return points.Count != 0 ? [] :
        [
            QualityValidationEngine.Finding("availability.empty-data", "availability", "critical", QualityStatuses.Critical,
                "No data returned",
                "The evaluation range returned no time-series points.",
                request.Start, request.End, expectedCount: expected.Count, actualCount: 0)
        ];
    }
}

public sealed class MissingTimestampsValidationPlugin() : QualityValidationPlugin(
    "completeness.missing-timestamps",
    "completeness",
    "Missing timestamps",
    "Checks expected timestamps over a half-open evaluation range.",
    "warning",
    new { granularity = "PT15M", maxMissingRatio = 0.0 })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var expected = QualityValidationEngine.ExpectedTimestamps(request.Start, request.End, request.Granularity, MarketCalendars.Resolve(request.Metadata.Calendar));
        if (expected.Count == 0)
        {
            return [];
        }

        var actual = Points(request)
            .Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End)
            .Select(x => x.Timestamp)
            .ToHashSet();
        var missing = expected.Where(x => !actual.Contains(x)).ToList();

        return missing.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("completeness.missing-timestamps", "completeness", "warning", QualityStatuses.Degraded,
                "Expected timestamps are missing",
                $"{missing.Count} of {expected.Count} expected timestamps are missing.",
                missing.First(), missing.Last().Add(request.Granularity), expected.Count, actual.Count, missing.Count, missing.Take(20))
        ];
    }
}

public sealed class TimestampAlignmentValidationPlugin() : QualityValidationPlugin(
    "timestamps.alignment",
    "timestamp_alignment",
    "Timestamp alignment",
    "Checks that timestamps align to the configured series timeframe.",
    "warning",
    new { granularity = "PT15M" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var misaligned = Points(request)
            .Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End)
            .Where(x => ((x.Timestamp - request.Start).Ticks % request.Granularity.Ticks) != 0)
            .Select(x => x.Timestamp)
            .ToList();

        return misaligned.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("timestamps.alignment", "timestamp_alignment", "warning", QualityStatuses.Degraded,
                "Timestamps are misaligned",
                $"{misaligned.Count} timestamps do not align to {request.Granularity}.",
                misaligned.First(), misaligned.Last(), affectedCount: misaligned.Count, samples: misaligned.Take(20))
        ];
    }
}

public sealed class FreshnessValidationPlugin() : QualityValidationPlugin(
    "freshness.latest-point",
    "freshness",
    "Latest point freshness",
    "Checks latest data against allowed delay, grace period, and publication expectations.",
    "critical",
    new { allowedDelay = "PT30M", gracePeriod = "PT10M", timeZone = "UTC" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var points = Points(request);
        if (points.Count == 0)
        {
            return [];
        }

        var latest = points.Max(x => x.Timestamp);
        var gracePeriod = ExecutionPluginConfiguration.GetOptionalDuration(context.Configuration, "gracePeriod") ?? TimeSpan.Zero;
        var allowedDelay = request.AllowedDelay ?? request.Granularity.Add(gracePeriod);
        var delay = request.Now - latest;
        return delay <= allowedDelay ? [] :
        [
            QualityValidationEngine.Finding("freshness.latest-point", "freshness", "critical", QualityStatuses.Critical,
                "Latest point is stale",
                $"Latest point is {delay} old; allowed delay is {allowedDelay} for {request.Metadata.Symbol} {request.Metadata.Timeframe}.",
                latest, request.Now, affectedCount: 1, samples: [latest],
                details: JsonSerializer.SerializeToElement(new { request.Metadata.Provider, request.Metadata.Symbol, request.Metadata.Timeframe, AllowedDelay = allowedDelay.ToString(), Delay = delay.ToString() }, QualityValidationEngine.JsonOptions))
        ];
    }
}

public sealed class DuplicateTimestampsValidationPlugin() : QualityValidationPlugin(
    "duplicates.timestamp-conflict",
    "duplicates",
    "Duplicate timestamps",
    "Checks duplicate timestamps and conflicting values when raw duplicate-preserving data is available.",
    "warning",
    new { })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var duplicates = Points(request)
            .GroupBy(x => x.Timestamp)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToList();

        return duplicates.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("duplicates.timestamp-conflict", "duplicates", "warning", QualityStatuses.Degraded,
                "Duplicate timestamps detected",
                $"{duplicates.Count} timestamps have multiple records.",
                duplicates.First(), duplicates.Last(), affectedCount: duplicates.Count, samples: duplicates.Take(20))
        ];
    }
}

public sealed class ValueRangeValidationPlugin() : QualityValidationPlugin(
    "validity.value-range",
    "value_validity",
    "Value validity",
    "Checks null, invalid numeric values, and configured min/max bounds.",
    "warning",
    new { min = (double?)null, max = (double?)null, allowNegative = true, allowZero = true })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var invalid = Points(request)
            .Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End)
            .Where(x => x.Value is null
                || double.IsNaN(x.Value.Value)
                || double.IsInfinity(x.Value.Value)
                || request.MinimumValue.HasValue && x.Value.Value < request.MinimumValue.Value
                || request.MaximumValue.HasValue && x.Value.Value > request.MaximumValue.Value)
            .Select(x => x.Timestamp)
            .ToList();

        var nullCount = Points(request).Count(x => x.Timestamp >= request.Start && x.Timestamp < request.End && x.Value is null);
        return invalid.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("validity.value-range", "value_validity", "warning", QualityStatuses.Degraded,
                nullCount == invalid.Count ? "Provider missing data" : "Values are invalid",
                nullCount == invalid.Count
                    ? $"{invalid.Count} provider data points have null values."
                    : $"{invalid.Count} values are null, non-finite, or outside configured bounds.",
                invalid.First(), invalid.Last(), affectedCount: invalid.Count, samples: invalid.Take(20))
        ];
    }
}

public sealed class OhlcConsistencyValidationPlugin() : QualityValidationPlugin(
    "validity.ohlc-consistency",
    "value_validity",
    "OHLC consistency",
    "Checks OHLC price positivity and candle ordering constraints.",
    "warning",
    new { requirePositivePrices = true, severity = "warning" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        if (!request.Metadata.MarketDataType.Equals("ohlcv", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var requirePositive = ValidationConfig.GetOptionalBool(context.Configuration, "requirePositivePrices") ?? true;
        var invalid = new List<(DateTimeOffset Timestamp, string Field, double Observed, string Expected)>();
        foreach (var point in Points(request).Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End))
        {
            if (requirePositive)
            {
                if (point.Open <= 0) invalid.Add((point.Timestamp, "open", point.Open, "> 0"));
                if (point.High <= 0) invalid.Add((point.Timestamp, "high", point.High, "> 0"));
                if (point.Low <= 0) invalid.Add((point.Timestamp, "low", point.Low, "> 0"));
                if (point.Close <= 0) invalid.Add((point.Timestamp, "close", point.Close, "> 0"));
            }

            if (point.High < point.Low) invalid.Add((point.Timestamp, "high", point.High, $">= low ({point.Low})"));
            if (point.High < point.Open) invalid.Add((point.Timestamp, "high", point.High, $">= open ({point.Open})"));
            if (point.High < point.Close) invalid.Add((point.Timestamp, "high", point.High, $">= close ({point.Close})"));
            if (point.Low > point.Open) invalid.Add((point.Timestamp, "low", point.Low, $"<= open ({point.Open})"));
            if (point.Low > point.Close) invalid.Add((point.Timestamp, "low", point.Low, $"<= close ({point.Close})"));
        }

        return invalid.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("validity.ohlc-consistency", "value_validity", "warning", QualityStatuses.Degraded,
                "OHLC bars are inconsistent",
                $"{invalid.Count} OHLC constraints failed. Example: {invalid[0].Timestamp:O} {invalid[0].Field} observed {invalid[0].Observed}, expected {invalid[0].Expected}.",
                invalid.Min(x => x.Timestamp), invalid.Max(x => x.Timestamp), affectedCount: invalid.Count, samples: invalid.Select(x => x.Timestamp).Distinct().Take(20),
                details: JsonSerializer.SerializeToElement(invalid.Take(50).Select(x => new { x.Timestamp, x.Field, x.Observed, x.Expected }), QualityValidationEngine.JsonOptions))
        ];
    }
}

public sealed class VolumeValidationPlugin() : QualityValidationPlugin(
    "validity.volume",
    "value_validity",
    "Volume validity",
    "Checks volume constraints. Zero volume is allowed by default.",
    "warning",
    new { allowZeroVolume = true, severity = "warning" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        if (!request.Metadata.MarketDataType.Equals("ohlcv", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var allowZero = ValidationConfig.GetOptionalBool(context.Configuration, "allowZeroVolume") ?? true;
        var invalid = Points(request)
            .Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End)
            .Where(x => x.Volume < 0 || !allowZero && x.Volume == 0)
            .Select(x => new { x.Timestamp, Field = "volume", Observed = x.Volume, Expected = allowZero ? ">= 0" : "> 0" })
            .ToList();

        return invalid.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("validity.volume", "value_validity", "warning", QualityStatuses.Degraded,
                "Volume is invalid",
                $"{invalid.Count} volume values violate the configured constraint. Example: {invalid[0].Timestamp:O} volume observed {invalid[0].Observed}, expected {invalid[0].Expected}.",
                invalid.First().Timestamp, invalid.Last().Timestamp, affectedCount: invalid.Count, samples: invalid.Select(x => x.Timestamp).Take(20),
                details: JsonSerializer.SerializeToElement(invalid.Take(50), QualityValidationEngine.JsonOptions))
        ];
    }
}

public sealed class PricePositiveValidationPlugin() : QualityValidationPlugin(
    "validity.price-positive",
    "value_validity",
    "Positive OHLC prices",
    "Checks that OHLC prices are positive for market-data bars.",
    "critical",
    new { severity = "critical" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        if (!request.Metadata.MarketDataType.Equals("ohlcv", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var invalid = new List<(DateTimeOffset Timestamp, string Field, double Observed, string Expected)>();
        foreach (var point in Points(request).Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End))
        {
            if (point.Open <= 0) invalid.Add((point.Timestamp, "open", point.Open, "> 0"));
            if (point.High <= 0) invalid.Add((point.Timestamp, "high", point.High, "> 0"));
            if (point.Low <= 0) invalid.Add((point.Timestamp, "low", point.Low, "> 0"));
            if (point.Close <= 0) invalid.Add((point.Timestamp, "close", point.Close, "> 0"));
        }

        return invalid.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("validity.price-positive", "value_validity", "critical", QualityStatuses.Critical,
                "OHLC prices are not positive",
                $"{invalid.Count} OHLC price values are not positive. Example: {invalid[0].Timestamp:O} {invalid[0].Field} observed {invalid[0].Observed}, expected {invalid[0].Expected}.",
                invalid.Min(x => x.Timestamp), invalid.Max(x => x.Timestamp), affectedCount: invalid.Count, samples: invalid.Select(x => x.Timestamp).Distinct().Take(20),
                details: JsonSerializer.SerializeToElement(invalid.Take(50), QualityValidationEngine.JsonOptions))
        ];
    }
}

public sealed class RateOfChangeValidationPlugin() : QualityValidationPlugin(
    "continuity.rate-of-change",
    "continuity",
    "Rate of change",
    "Checks maximum absolute and percentage change between consecutive values.",
    "warning",
    new { maxAbsoluteChange = (double?)null, maxPercentageChange = (double?)null, nearZeroFloor = 0.000001 })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        if (request.MaximumAbsoluteChange is null && request.MaximumPercentageChange is null)
        {
            return [];
        }

        var bad = new List<DateTimeOffset>();
        var finite = Points(request).Where(x => x.Value.HasValue && !double.IsNaN(x.Value.Value) && !double.IsInfinity(x.Value.Value)).ToList();
        for (var i = 1; i < finite.Count; i++)
        {
            var previous = finite[i - 1].Value!.Value;
            var current = finite[i].Value!.Value;
            var absolute = Math.Abs(current - previous);
            var denominator = Math.Max(Math.Abs(previous), request.NearZeroFloor);
            var percentage = absolute / denominator * 100;

            if (request.MaximumAbsoluteChange.HasValue && absolute > request.MaximumAbsoluteChange.Value
                || request.MaximumPercentageChange.HasValue && percentage > request.MaximumPercentageChange.Value)
            {
                bad.Add(finite[i].Timestamp);
            }
        }

        return bad.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("continuity.rate-of-change", "continuity", "warning", QualityStatuses.Degraded,
                "Rate of change exceeded",
                $"{bad.Count} consecutive changes exceed configured limits.",
                bad.First(), bad.Last(), affectedCount: bad.Count, samples: bad.Take(20))
        ];
    }
}

public sealed class FlatLineValidationPlugin() : QualityValidationPlugin(
    "stale.flat-line",
    "stale_values",
    "Flat line",
    "Checks repeated or near-constant values over a configured window.",
    "warning",
    new { window = "PT2H", varianceThreshold = 0.0 })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        if (request.FlatLinePointCount < 2)
        {
            return [];
        }

        var finite = Points(request).Where(x => x.Value.HasValue && !double.IsNaN(x.Value.Value) && !double.IsInfinity(x.Value.Value)).ToList();
        var run = new List<DateTimeOffset>();
        for (var i = 1; i < finite.Count; i++)
        {
            if (finite[i].Value == finite[i - 1].Value)
            {
                if (run.Count == 0)
                {
                    run.Add(finite[i - 1].Timestamp);
                }

                run.Add(finite[i].Timestamp);
                continue;
            }

            if (run.Count >= request.FlatLinePointCount)
            {
                break;
            }

            run.Clear();
        }

        return run.Count < request.FlatLinePointCount ? [] :
        [
            QualityValidationEngine.Finding("stale.flat-line", "stale_values", "warning", QualityStatuses.Degraded,
                "Values are flat",
                $"{run.Count} consecutive values are unchanged.",
                run.First(), run.Last(), affectedCount: run.Count, samples: run.Take(20))
        ];
    }
}

public sealed class PriceSpikeValidationPlugin() : QualityValidationPlugin(
    "anomaly.price-spike",
    "anomaly",
    "Price spike",
    "Checks close-price spikes against rolling z-score and percentage-change thresholds.",
    "warning",
    new { rollingWindow = 20, minimumObservations = 10, zScoreThreshold = 4.0, percentageChangeThreshold = 15.0, severity = "warning" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var window = ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "rollingWindow") ?? 20;
        var minimum = ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "minimumObservations") ?? Math.Min(10, window);
        var zThreshold = ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "zScoreThreshold") ?? 4.0;
        var pctThreshold = ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "percentageChangeThreshold") ?? 15.0;
        var points = Points(request).Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End && MarketValidationStatistics.IsFinite(x.Close)).ToList();
        if (points.Count < minimum + 1)
        {
            return [QualityValidationEngine.InsufficientData("anomaly.price-spike", "Price spike skipped", $"Price spike validation needs at least {minimum + 1} observations; found {points.Count}.", request, points.Count)];
        }

        var spikes = MarketValidationStatistics.RollingSpike(points.Select(x => (x.Timestamp, Value: x.Close)).ToList(), window, minimum, zThreshold, pctThreshold, request.NearZeroFloor);

        return spikes.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("anomaly.price-spike", "anomaly", "warning", QualityStatuses.Warning,
                "Price spike detected",
                $"{spikes.Count} close-price observations exceeded rolling z-score or percentage-change thresholds.",
                spikes.First().Timestamp, spikes.Last().Timestamp, affectedCount: spikes.Count, samples: spikes.Select(x => x.Timestamp).Take(20),
                details: JsonSerializer.SerializeToElement(spikes.Take(50), QualityValidationEngine.JsonOptions))
        ];
    }
}

public sealed class VolumeSpikeValidationPlugin() : QualityValidationPlugin(
    "anomaly.volume-spike",
    "anomaly",
    "Volume spike",
    "Checks volume spikes against rolling z-score and percentage-change thresholds.",
    "warning",
    new { rollingWindow = 20, minimumObservations = 10, zScoreThreshold = 5.0, percentageChangeThreshold = 300.0, severity = "warning" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var window = ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "rollingWindow") ?? 20;
        var minimum = ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "minimumObservations") ?? Math.Min(10, window);
        var zThreshold = ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "zScoreThreshold") ?? 5.0;
        var pctThreshold = ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "percentageChangeThreshold") ?? 300.0;
        var points = Points(request).Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End && MarketValidationStatistics.IsFinite(x.Volume)).ToList();
        if (points.Count < minimum + 1)
        {
            return [QualityValidationEngine.InsufficientData("anomaly.volume-spike", "Volume spike skipped", $"Volume spike validation needs at least {minimum + 1} observations; found {points.Count}.", request, points.Count)];
        }

        var spikes = MarketValidationStatistics.RollingSpike(points.Select(x => (x.Timestamp, Value: x.Volume)).ToList(), window, minimum, zThreshold, pctThreshold, request.NearZeroFloor);

        return spikes.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("anomaly.volume-spike", "anomaly", "warning", QualityStatuses.Warning,
                "Volume spike detected",
                $"{spikes.Count} volume observations exceeded rolling z-score or percentage-change thresholds.",
                spikes.First().Timestamp, spikes.Last().Timestamp, affectedCount: spikes.Count, samples: spikes.Select(x => x.Timestamp).Take(20),
                details: JsonSerializer.SerializeToElement(spikes.Take(50), QualityValidationEngine.JsonOptions))
        ];
    }
}

public sealed class FlatPriceValidationPlugin() : QualityValidationPlugin(
    "stale.flat-price",
    "stale_values",
    "Flat price",
    "Checks repeated close prices over a configured observation count.",
    "warning",
    new { minimumConsecutiveBars = 5, tolerance = 0.0, severity = "warning" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var minimum = ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "minimumConsecutiveBars") ?? 5;
        var tolerance = ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "tolerance") ?? 0.0;
        if (minimum < 2)
        {
            return [];
        }

        var points = Points(request).Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End && MarketValidationStatistics.IsFinite(x.Close)).ToList();
        var run = MarketValidationStatistics.LongestFlatRun(points.Select(x => (x.Timestamp, Value: x.Close)).ToList(), tolerance);
        return run.Count < minimum ? [] :
        [
            QualityValidationEngine.Finding("stale.flat-price", "stale_values", "warning", QualityStatuses.Warning,
                "Close price is flat",
                $"{run.Count} consecutive close prices changed by no more than {tolerance}.",
                run.First(), run.Last(), affectedCount: run.Count, samples: run.Take(20))
        ];
    }
}

public sealed class AbnormalVolatilityValidationPlugin() : QualityValidationPlugin(
    "anomaly.abnormal-volatility",
    "anomaly",
    "Abnormal volatility",
    "Checks absolute returns against a rolling return-volatility z-score.",
    "warning",
    new { rollingWindow = 20, minimumObservations = 10, zScoreThreshold = 4.0, severity = "warning" })
{
    internal override List<QualityFindingDraftDto> Evaluate(ExecutionPluginContext context, QualityEvaluationRequest request)
    {
        var window = ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "rollingWindow") ?? 20;
        var minimum = ExecutionPluginConfiguration.GetOptionalInt(context.Configuration, "minimumObservations") ?? Math.Min(10, window);
        var zThreshold = ExecutionPluginConfiguration.GetOptionalDouble(context.Configuration, "zScoreThreshold") ?? 4.0;
        var points = Points(request).Where(x => x.Timestamp >= request.Start && x.Timestamp < request.End && MarketValidationStatistics.IsFinite(x.Close) && x.Close > 0).ToList();
        if (points.Count < minimum + 2)
        {
            return [QualityValidationEngine.InsufficientData("anomaly.abnormal-volatility", "Abnormal volatility skipped", $"Abnormal volatility validation needs at least {minimum + 2} observations; found {points.Count}.", request, points.Count)];
        }

        var returns = new List<(DateTimeOffset Timestamp, double Value)>();
        for (var i = 1; i < points.Count; i++)
        {
            returns.Add((points[i].Timestamp, Math.Abs(points[i].Close / points[i - 1].Close - 1.0)));
        }

        var spikes = MarketValidationStatistics.RollingSpike(returns, window, minimum, zThreshold, null, request.NearZeroFloor);
        return spikes.Count == 0 ? [] :
        [
            QualityValidationEngine.Finding("anomaly.abnormal-volatility", "anomaly", "warning", QualityStatuses.Warning,
                "Abnormal volatility detected",
                $"{spikes.Count} returns exceeded the rolling volatility z-score threshold.",
                spikes.First().Timestamp, spikes.Last().Timestamp, affectedCount: spikes.Count, samples: spikes.Select(x => x.Timestamp).Take(20),
                details: JsonSerializer.SerializeToElement(spikes.Take(50), QualityValidationEngine.JsonOptions))
        ];
    }
}

internal record RollingSpikeFinding(DateTimeOffset Timestamp, double Observed, double Mean, double StandardDeviation, double? ZScore, double? PercentageChange);

internal static class MarketValidationStatistics
{
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    public static List<RollingSpikeFinding> RollingSpike(
        List<(DateTimeOffset Timestamp, double Value)> points,
        int window,
        int minimumObservations,
        double zScoreThreshold,
        double? percentageChangeThreshold,
        double nearZeroFloor)
    {
        var result = new List<RollingSpikeFinding>();
        for (var i = 1; i < points.Count; i++)
        {
            var start = Math.Max(0, i - Math.Max(window, 1));
            var history = points.Skip(start).Take(i - start).Select(x => x.Value).ToList();
            if (history.Count < minimumObservations)
            {
                continue;
            }

            var mean = history.Average();
            var variance = history.Sum(x => Math.Pow(x - mean, 2)) / history.Count;
            var stdDev = Math.Sqrt(variance);
            var current = points[i].Value;
            var zScore = stdDev <= nearZeroFloor ? (double?)null : Math.Abs((current - mean) / stdDev);
            var previous = points[i - 1].Value;
            var percentage = Math.Abs(current - previous) / Math.Max(Math.Abs(previous), nearZeroFloor) * 100;
            if (zScore >= zScoreThreshold || percentageChangeThreshold.HasValue && percentage >= percentageChangeThreshold.Value)
            {
                result.Add(new RollingSpikeFinding(points[i].Timestamp, current, mean, stdDev, zScore, percentage));
            }
        }

        return result;
    }

    public static List<DateTimeOffset> LongestFlatRun(List<(DateTimeOffset Timestamp, double Value)> points, double tolerance)
    {
        var best = new List<DateTimeOffset>();
        var current = new List<DateTimeOffset>();
        for (var i = 1; i < points.Count; i++)
        {
            if (Math.Abs(points[i].Value - points[i - 1].Value) <= tolerance)
            {
                if (current.Count == 0)
                {
                    current.Add(points[i - 1].Timestamp);
                }

                current.Add(points[i].Timestamp);
                if (current.Count > best.Count)
                {
                    best = [.. current];
                }
                continue;
            }

            current.Clear();
        }

        return best;
    }
}

internal static class ValidationPlugins
{
    public static readonly List<QualityValidationPlugin> All =
    [
        new RequiredMetadataValidationPlugin(),
        new EmptyDataValidationPlugin(),
        new MissingTimestampsValidationPlugin(),
        new TimestampAlignmentValidationPlugin(),
        new FreshnessValidationPlugin(),
        new DuplicateTimestampsValidationPlugin(),
        new ValueRangeValidationPlugin(),
        new OhlcConsistencyValidationPlugin(),
        new VolumeValidationPlugin(),
        new RateOfChangeValidationPlugin(),
        new FlatLineValidationPlugin(),
        new PriceSpikeValidationPlugin(),
        new VolumeSpikeValidationPlugin(),
        new FlatPriceValidationPlugin(),
        new AbnormalVolatilityValidationPlugin()
    ];
}
