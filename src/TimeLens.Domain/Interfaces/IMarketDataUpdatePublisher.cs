using TimeLens.Domain.Models;

namespace TimeLens.Domain.Interfaces;

public interface IMarketDataUpdatePublisher
{
    Task PublishAsync(string datasetId, TimeSeriesWritePoint point, DateTimeOffset asOf, string operation, CancellationToken cancellationToken = default);
}
