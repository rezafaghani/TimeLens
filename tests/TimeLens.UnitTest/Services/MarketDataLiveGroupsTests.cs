using TimeLens.API.Infrastructure.Services;
using TimeLens.Domain.Models;

namespace TimeLens.UnitTest.Services;

public class MarketDataLiveGroupsTests
{
    [Fact]
    public void For_UsesDatasetIdentityForSubscriptionGroup()
    {
        var group = MarketDataLiveGroups.For(new MarketDataUpdatedEvent
        {
            DatasetId = "coinbase-exchange:coinbase:ETH-USD:ohlcv:15m",
            SeriesId = "coinbase:eth-usd:15m",
            ProviderId = "coinbase-exchange",
            Symbol = "ETH-USD",
            MarketDataType = "ohlcv",
            Timeframe = "15m"
        });

        Assert.Equal("market-data:coinbase-exchange:coinbase:eth-usd:ohlcv:15m", group);
    }
}
