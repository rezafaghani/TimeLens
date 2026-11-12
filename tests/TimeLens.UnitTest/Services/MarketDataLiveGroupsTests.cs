using TimeLens.API.Infrastructure.Services;
using TimeLens.Domain.Models;

namespace TimeLens.UnitTest.Services;

public class MarketDataLiveGroupsTests
{
    [Fact]
    public void For_UsesCanonicalSeriesIdentityForSubscriptionGroup()
    {
        var group = MarketDataLiveGroups.For(new MarketDataUpdatedEvent
        {
            SeriesId = 12,
            ProviderId = "coinbase-exchange",
            Symbol = "ETH-USD",
            MarketDataType = "ohlcv",
            Timeframe = "15m"
        });

        Assert.Equal("market-data:12", group);
    }
}
