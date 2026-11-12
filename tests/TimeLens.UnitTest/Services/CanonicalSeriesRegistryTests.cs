using TimeLens.Domain.Models;

namespace TimeLens.UnitTest.Services;

public class CanonicalSeriesRegistryTests
{
    [Theory]
    [InlineData(1, "energy-charts", "energy-charts:dk1:day-ahead-price", "1h")]
    [InlineData(2, "energy-charts", "energy-charts:dk1:day-ahead-price", "15m")]
    [InlineData(5, "coinbase-exchange", "coinbase:btc-usd:1m", "1m")]
    [InlineData(17, "coin-metrics-community", "coinmetrics:btc:txcnt:1d", "1d")]
    public void Resolve_ReturnsTheCanonicalSeriesId(
        long seriesId,
        string provider,
        string providerSeriesId,
        string timeframe)
    {
        var definition = CanonicalSeriesRegistry.Resolve(provider, providerSeriesId, timeframe);

        Assert.Equal(seriesId, definition.SeriesId);
        Assert.Equal(definition, CanonicalSeriesRegistry.Get(seriesId));
    }

    [Fact]
    public void Resolve_RejectsDefinitionsNotInTheRegistry()
    {
        Assert.Throws<InvalidOperationException>(() =>
            CanonicalSeriesRegistry.Resolve("coinbase-exchange", "coinbase:sol-usd:1m", "1m"));
    }

    [Fact]
    public void ResolveSchedule_UsesProviderParametersAndRejectsUnregisteredDefinitions()
    {
        var definition = CanonicalSeriesRegistry.ResolveSchedule(
            "coinbase-exchange",
            new Dictionary<string, string>
            {
                ["product_id"] = "BTC-USD",
                ["timeframe"] = "1m"
            });

        Assert.Equal(5, definition.SeriesId);
        Assert.Throws<InvalidOperationException>(() =>
            CanonicalSeriesRegistry.ResolveSchedule(
                "coinbase-exchange",
                new Dictionary<string, string>
                {
                    ["product_id"] = "SOL-USD",
                    ["timeframe"] = "1m"
                }));
    }

    [Fact]
    public void Registry_UsesUniqueNumericIds()
    {
        Assert.Equal(
            CanonicalSeriesRegistry.All.Count,
            CanonicalSeriesRegistry.All.Select(definition => definition.SeriesId).Distinct().Count());
    }
}
