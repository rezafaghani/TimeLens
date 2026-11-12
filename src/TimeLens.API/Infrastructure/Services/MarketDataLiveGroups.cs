using TimeLens.Domain.Models;

namespace TimeLens.API.Infrastructure.Services;

public static class MarketDataLiveGroups
{
    public static string For(MarketDataUpdatedEvent message) => For(message.SeriesId);

    public static string For(long seriesId) =>
        $"market-data:{seriesId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
