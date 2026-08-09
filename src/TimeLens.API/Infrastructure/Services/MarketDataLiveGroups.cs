using TimeLens.Domain.Models;

namespace TimeLens.API.Infrastructure.Services;

public static class MarketDataLiveGroups
{
    public static string For(MarketDataUpdatedEvent message) =>
        For(message.DatasetId, message.SeriesId, message.ProviderId, message.Symbol, message.MarketDataType, message.Timeframe);

    public static string For(string datasetId, string seriesId, string providerId, string symbol, string marketDataType, string timeframe)
    {
        var key = string.IsNullOrWhiteSpace(datasetId)
            ? $"{providerId}:{symbol}:{marketDataType}:{timeframe}"
            : datasetId;
        return $"market-data:{key.Trim().ToLowerInvariant()}";
    }
}
