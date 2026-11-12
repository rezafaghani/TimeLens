using TimeLens.Domain.Models;

namespace TimeLens.Domain.Interfaces;

public interface IMarketDataUpdatePublisher
{
    Task PublishBatchAsync(
        long seriesId,
        IReadOnlyList<TimeSeriesWritePoint> points,
        DateTimeOffset asOf,
        string operation,
        CancellationToken cancellationToken = default);
}
