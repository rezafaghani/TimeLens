namespace TimeLens.Domain.Models;

public record MarketCorrelationResult(
    string LeftDatasetId,
    string RightDatasetId,
    string Bucket,
    int Samples,
    double? Correlation,
    IReadOnlyList<LagCorrelation> Lags);

public record LagCorrelation(int Lag, string Offset, int Samples, double? Correlation);

public static class MarketCorrelation
{
    public static MarketCorrelationResult Calculate(
        string leftDatasetId,
        IReadOnlyCollection<TimeSeriesPointDto> left,
        string rightDatasetId,
        IReadOnlyCollection<TimeSeriesPointDto> right,
        TimeSpan bucket,
        int maxLag)
    {
        var leftChanges = Changes(left, bucket);
        var rightChanges = Changes(right, bucket);
        var lags = Enumerable.Range(-maxLag, maxLag * 2 + 1)
            .Select(lag => Correlate(leftChanges, rightChanges, lag, bucket))
            .ToList();
        var current = lags[maxLag];

        return new MarketCorrelationResult(
            leftDatasetId,
            rightDatasetId,
            Format(bucket),
            current.Samples,
            current.Correlation,
            lags);
    }

    private static Dictionary<DateTimeOffset, double> Changes(
        IReadOnlyCollection<TimeSeriesPointDto> points,
        TimeSpan bucket)
    {
        var closes = points
            .GroupBy(point => Floor(point.Timestamp, bucket))
            .ToDictionary(group => group.Key, group => group.MaxBy(point => point.Timestamp)!.Close);
        var ordered = closes.OrderBy(pair => pair.Key).ToList();
        var result = new Dictionary<DateTimeOffset, double>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1].Value;
            if (previous != 0 && ordered[i].Key - ordered[i - 1].Key == bucket)
            {
                result[ordered[i].Key] = (ordered[i].Value - previous) / Math.Abs(previous);
            }
        }

        return result;
    }

    private static LagCorrelation Correlate(
        IReadOnlyDictionary<DateTimeOffset, double> left,
        IReadOnlyDictionary<DateTimeOffset, double> right,
        int lag,
        TimeSpan bucket)
    {
        var pairs = left
            .Select(pair => (Left: pair.Value, RightTime: pair.Key.AddTicks(bucket.Ticks * lag)))
            .Where(pair => right.ContainsKey(pair.RightTime))
            .Select(pair => (pair.Left, Right: right[pair.RightTime]))
            .ToList();

        return new LagCorrelation(lag, Format(bucket * lag), pairs.Count, Pearson(pairs));
    }

    private static double? Pearson(IReadOnlyCollection<(double Left, double Right)> pairs)
    {
        if (pairs.Count < 2)
        {
            return null;
        }

        var leftMean = pairs.Average(pair => pair.Left);
        var rightMean = pairs.Average(pair => pair.Right);
        var numerator = pairs.Sum(pair => (pair.Left - leftMean) * (pair.Right - rightMean));
        var denominator = Math.Sqrt(
            pairs.Sum(pair => Math.Pow(pair.Left - leftMean, 2))
            * pairs.Sum(pair => Math.Pow(pair.Right - rightMean, 2)));
        return denominator == 0 ? null : numerator / denominator;
    }

    private static DateTimeOffset Floor(DateTimeOffset timestamp, TimeSpan bucket)
    {
        var utcTicks = timestamp.UtcTicks;
        return new DateTimeOffset(utcTicks - utcTicks % bucket.Ticks, TimeSpan.Zero);
    }

    private static string Format(TimeSpan value) => value.TotalDays is >= 1 or <= -1
        ? $"{value.TotalDays:0.##}d"
        : value.TotalHours is >= 1 or <= -1
            ? $"{value.TotalHours:0.##}h"
            : $"{value.TotalMinutes:0.##}m";
}
