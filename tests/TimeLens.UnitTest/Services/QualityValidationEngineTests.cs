using TimeLens.Domain.Models;
using TimeLens.Domain.Services;
using TimeLens.Validation;

namespace TimeLens.UnitTest.Services;

public class QualityValidationEngineTests
{
    [Fact]
    public void ExpectedTimestamps_UsesHalfOpenRange()
    {
        var start = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var end = DateTimeOffset.Parse("2026-01-01T11:00:00Z");

        var expected = QualityValidationEngine.ExpectedTimestamps(start, end, TimeSpan.FromMinutes(15));

        Assert.Equal([
            DateTimeOffset.Parse("2026-01-01T10:00:00Z"),
            DateTimeOffset.Parse("2026-01-01T10:15:00Z"),
            DateTimeOffset.Parse("2026-01-01T10:30:00Z"),
            DateTimeOffset.Parse("2026-01-01T10:45:00Z")
        ], expected);
    }

    [Fact]
    public void Evaluate_DetectsMissingAndMisalignedTimestampsSeparately()
    {
        var request = Request([
            Point("2026-01-01T10:00:00Z", 1),
            Point("2026-01-01T10:07:00Z", 2),
            Point("2026-01-01T10:30:00Z", 3),
            Point("2026-01-01T10:45:00Z", 4)
        ]);

        var findings = QualityValidationEngine.Evaluate(request);

        Assert.Contains(findings, x => x.ValidatorId == "completeness.missing-timestamps" && x.AffectedCount == 1);
        Assert.Contains(findings, x => x.ValidatorId == "timestamps.alignment" && x.AffectedCount == 1);
    }

    [Fact]
    public void Evaluate_DetectsFreshnessAndDuplicateAndInvalidValues()
    {
        var request = Request([
            Point("2026-01-01T10:00:00Z", 1),
            Point("2026-01-01T10:00:00Z", 2),
            Point("2026-01-01T10:15:00Z", null)
        ]) with
        {
            Now = DateTimeOffset.Parse("2026-01-01T12:00:00Z"),
            AllowedDelay = TimeSpan.FromMinutes(30)
        };

        var findings = QualityValidationEngine.Evaluate(request);

        Assert.Contains(findings, x => x.ValidatorId == "freshness.latest-point");
        Assert.Contains(findings, x => x.ValidatorId == "duplicates.timestamp-conflict");
        Assert.Contains(findings, x => x.ValidatorId == "validity.ohlc-consistency");
    }

    [Fact]
    public void Evaluate_DetectsRateOfChangeAndFlatLine()
    {
        var request = Request([
            Point("2026-01-01T10:00:00Z", 1),
            Point("2026-01-01T10:15:00Z", 1),
            Point("2026-01-01T10:30:00Z", 1),
            Point("2026-01-01T10:45:00Z", 20)
        ]) with
        {
            MaximumAbsoluteChange = 5,
            FlatLinePointCount = 3
        };

        var findings = QualityValidationEngine.Evaluate(request);

        Assert.Contains(findings, x => x.ValidatorId == "continuity.rate-of-change");
        Assert.Contains(findings, x => x.ValidatorId == "stale.flat-line");
    }

    [Fact]
    public void Evaluate_DetectsInvalidOhlcvBars()
    {
        var request = Request([
            Bar("2026-01-01T10:00:00Z", 10, 9, 8, 11, -1)
        ]);

        var findings = QualityValidationEngine.Evaluate(request);

        Assert.Contains(findings, x => x.ValidatorId == "validity.ohlc-consistency");
        Assert.Contains(findings, x => x.ValidatorId == "validity.volume");
    }

    [Fact]
    public void Evaluate_AllowsZeroVolumeByDefault()
    {
        var request = Request([
            Bar("2026-01-01T10:00:00Z", 10, 11, 9, 10.5, 0)
        ]);

        var findings = QualityValidationEngine.Evaluate(request);

        Assert.DoesNotContain(findings, x => x.ValidatorId == "validity.volume");
    }

    [Fact]
    public void Evaluate_DetectsPriceAndVolumeSpikes()
    {
        var bars = Enumerable.Range(0, 12)
            .Select(i => Bar(DateTimeOffset.Parse("2026-01-01T10:00:00Z").AddMinutes(i).ToString("O"), 100, 101, 99, 100, 10))
            .ToList();
        bars.Add(Bar("2026-01-01T10:12:00Z", 300, 301, 299, 300, 1000));

        var findings = QualityValidationEngine.Evaluate(Request(bars, end: "2026-01-01T10:13:00Z", granularity: TimeSpan.FromMinutes(1)));

        Assert.Contains(findings, x => x.ValidatorId == "anomaly.price-spike");
        Assert.Contains(findings, x => x.ValidatorId == "anomaly.volume-spike");
    }

    [Fact]
    public void Evaluate_DetectsFlatPrice()
    {
        var bars = Enumerable.Range(0, 6)
            .Select(i => Bar(DateTimeOffset.Parse("2026-01-01T10:00:00Z").AddMinutes(i).ToString("O"), 100, 101, 99, 100, 10))
            .ToList();

        var findings = QualityValidationEngine.Evaluate(Request(bars, end: "2026-01-01T10:06:00Z", granularity: TimeSpan.FromMinutes(1)));

        Assert.Contains(findings, x => x.ValidatorId == "stale.flat-price");
    }

    [Fact]
    public void Evaluate_DetectsAbnormalVolatility()
    {
        var bars = Enumerable.Range(0, 12)
            .Select(i =>
            {
                var close = 100 + i;
                return Bar(DateTimeOffset.Parse("2026-01-01T10:00:00Z").AddMinutes(i).ToString("O"), close, close + 1, close - 1, close, 10);
            })
            .ToList();
        bars.Add(Bar("2026-01-01T10:12:00Z", 200, 201, 199, 200, 10));

        var findings = QualityValidationEngine.Evaluate(Request(bars, end: "2026-01-01T10:13:00Z", granularity: TimeSpan.FromMinutes(1)));

        Assert.Contains(findings, x => x.ValidatorId == "anomaly.abnormal-volatility");
    }

    [Fact]
    public void Evaluate_ReportsInsufficientDataForStatisticalValidators()
    {
        var findings = QualityValidationEngine.Evaluate(Request([
            Bar("2026-01-01T10:00:00Z", 10, 11, 9, 10, 1)
        ]));

        Assert.Contains(findings, x => x.ValidatorId == "anomaly.price-spike" && x.QualityStatus == QualityStatuses.InsufficientData);
    }

    [Fact]
    public void ValidationPlugins_ExposeMarketValidatorsForDiscovery()
    {
        var pluginIds = typeof(QualityValidationEngine).Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false } && typeof(IExecutionPlugin).IsAssignableFrom(type))
            .Select(type => ((IExecutionPlugin)Activator.CreateInstance(type)!).Metadata.Id)
            .ToHashSet();

        Assert.Contains("timelens.validation.validity.ohlc-consistency", pluginIds);
        Assert.Contains("timelens.validation.validity.price-positive", pluginIds);
        Assert.Contains("timelens.validation.validity.volume", pluginIds);
        Assert.Contains("timelens.validation.anomaly.price-spike", pluginIds);
        Assert.Contains("timelens.validation.anomaly.volume-spike", pluginIds);
        Assert.Contains("timelens.validation.anomaly.abnormal-volatility", pluginIds);
    }

    private static QualityEvaluationRequest Request(List<TimeSeriesPointDto> points, string end = "2026-01-01T11:00:00Z", TimeSpan? granularity = null) => new(
        new DatasetMetadataDto
        {
            Id = "dataset-1",
            SeriesId = "coinbase:btc-usd:15m",
            Provider = "coinbase-exchange",
            Exchange = "Coinbase",
            Symbol = "BTC-USD",
            AssetClass = "Crypto",
            BaseAsset = "BTC",
            QuoteAsset = "USD",
            MarketDataType = "ohlcv",
            Timeframe = "15m",
            Unit = "USD",
            Calendar = "crypto-24x7"
        },
        DateTimeOffset.Parse("2026-01-01T10:00:00Z"),
        DateTimeOffset.Parse(end),
        DateTimeOffset.Parse("2026-01-01T11:00:00Z"),
        granularity ?? TimeSpan.FromMinutes(15),
        null,
        null,
        null,
        null,
        null,
        0.000001,
        0,
        points);

    private static TimeSeriesPointDto Point(string timestamp, double? value) => new()
    {
        Timestamp = DateTimeOffset.Parse(timestamp),
        Open = value ?? 0,
        High = value ?? 0,
        Low = value ?? 0,
        Close = value ?? 0,
        Volume = 1,
        AsOf = DateTimeOffset.Parse("2026-01-01T12:00:00Z")
    };

    private static TimeSeriesPointDto Bar(string timestamp, double open, double high, double low, double close, double volume) => new()
    {
        Timestamp = DateTimeOffset.Parse(timestamp),
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = volume,
        AsOf = DateTimeOffset.Parse("2026-01-01T12:00:00Z")
    };
}
