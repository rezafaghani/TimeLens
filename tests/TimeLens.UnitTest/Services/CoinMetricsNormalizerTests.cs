using System.Text.Json;
using TimeLens.Domain.Models;
using TimeLens.Ingestion;
using TimeLens.Ingestion.Services;

namespace TimeLens.UnitTest.Services;

public class CoinMetricsNormalizerTests
{
    [Fact]
    public void Normalize_MapsNetworkMetricToFlatBar()
    {
        using var document = JsonDocument.Parse("""
            {"data":[{"asset":"btc","time":"2026-09-18T00:00:00.000000000Z","AdrActCnt":"812345"}]}
            """);

        var dataset = new CoinMetricsNormalizer().Normalize(new MarketDataRequestDefinition
        {
            Endpoint = "timeseries/asset-metrics",
            Parameters = new Dictionary<string, string>
            {
                ["asset"] = "btc",
                ["metric"] = "AdrActCnt",
                ["frequency"] = "1d"
            }
        }, document.RootElement);

        Assert.Equal("coinmetrics:btc:adractcnt:1d", dataset.Metadata.SeriesId);
        Assert.Equal("coin-metrics-community", dataset.Metadata.Provider);
        Assert.Equal("BTC-AdrActCnt", dataset.Metadata.Symbol);
        Assert.Equal("network-metric", dataset.Metadata.MarketDataType);
        Assert.Equal("Addresses", dataset.Metadata.Unit);
        Assert.Equal(812345, dataset.Batch.Points.Single().Close);
    }
}
