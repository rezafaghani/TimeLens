using TimeLens.Domain.Models;

namespace TimeLens.UnitTest.Services;

public class MarketCorrelationTests
{
    [Fact]
    public void Calculate_FindsRightSeriesLaggingLeft()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var left = Points(start, 100, 110, 99, 118.8, 95.04);
        var right = Points(start, 50, 50, 55, 49.5, 59.4, 47.52);

        var result = MarketCorrelation.Calculate("crypto", left, "energy", right, TimeSpan.FromHours(1), 2);

        var oneHourLag = Assert.Single(result.Lags, item => item.Lag == 1);
        Assert.Equal(4, oneHourLag.Samples);
        Assert.Equal(1, oneHourLag.Correlation!.Value, 10);
    }

    private static List<TimeSeriesPointDto> Points(DateTimeOffset start, params double[] closes) => closes
        .Select((close, index) => new TimeSeriesPointDto { Timestamp = start.AddHours(index), Close = close })
        .ToList();
}
