using System.Text.Json;
using TimeLens.Ingestion;
using TimeLens.Ingestion.Services;

namespace TimeLens.UnitTest.Services;

public class CoinbaseCandleNormalizerTests
{
    [Fact]
    public void Normalize_MapsCoinbaseCandlesToOhlcvBars()
    {
        using var document = JsonDocument.Parse("""
            [[1704067200,42000,43000,42500,42800,12.5]]
            """);

        var dataset = new CoinbaseCandleNormalizer().Normalize(new MarketDataRequestDefinition
        {
            Endpoint = "products/candles",
            Parameters = new Dictionary<string, string>
            {
                ["product_id"] = "BTC-USD",
                ["timeframe"] = "1h",
                ["granularity"] = "3600"
            }
        }, document.RootElement);

        Assert.Equal("coinbase-exchange", dataset.Metadata.Provider);
        Assert.Equal("BTC-USD", dataset.Metadata.Symbol);
        Assert.Equal("BTC", dataset.Metadata.BaseAsset);
        Assert.Equal("USD", dataset.Metadata.QuoteAsset);
        Assert.Equal("ohlcv", dataset.Metadata.MarketDataType);
        Assert.Equal("1h", dataset.Metadata.Timeframe);
        Assert.Equal(42500, dataset.Batch.Points[0].Open);
        Assert.Equal(43000, dataset.Batch.Points[0].High);
        Assert.Equal(42000, dataset.Batch.Points[0].Low);
        Assert.Equal(42800, dataset.Batch.Points[0].Close);
        Assert.Equal(12.5, dataset.Batch.Points[0].Volume);
    }
}
