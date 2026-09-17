using System.Text.Json;
using TimeLens.Ingestion;
using TimeLens.Ingestion.Services;

namespace TimeLens.UnitTest.Services;

public class EnergyChartsPriceNormalizerTests
{
    [Fact]
    public void Normalize_MapsPricesToFlatBars()
    {
        using var document = JsonDocument.Parse("""
            {"license_info":"CC BY 4.0","unix_seconds":[1704067200],"price":[51.25],"unit":"EUR / MWh","deprecated":false}
            """);

        var dataset = new EnergyChartsPriceNormalizer().Normalize(new MarketDataRequestDefinition
        {
            Endpoint = "price",
            Parameters = new Dictionary<string, string>
            {
                ["bzn"] = "DK1",
                ["timeframe"] = "1h"
            }
        }, document.RootElement);

        Assert.Equal("energy-charts", dataset.Metadata.Provider);
        Assert.Equal("DK1", dataset.Metadata.Symbol);
        Assert.Equal("Energy", dataset.Metadata.AssetClass);
        Assert.Equal("price", dataset.Metadata.MarketDataType);
        Assert.Equal("EUR / MWh", dataset.Metadata.Unit);
        Assert.Equal(51.25, dataset.Batch.Points[0].Open);
        Assert.Equal(51.25, dataset.Batch.Points[0].High);
        Assert.Equal(51.25, dataset.Batch.Points[0].Low);
        Assert.Equal(51.25, dataset.Batch.Points[0].Close);
        Assert.Equal(0, dataset.Batch.Points[0].Volume);
    }
}
