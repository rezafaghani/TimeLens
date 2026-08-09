using TimeLens.Domain.Models;

namespace TimeLens.Domain.Interfaces;

public interface IMarketDataReader
{
    Task<MarketDataReadResult?> ReadAsync(
        string targetType,
        string targetId,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset? asOf = null,
        CancellationToken cancellationToken = default);
}

public record MarketDataReadResult(DatasetMetadataDto Metadata, List<TimeSeriesPointDto> Points);
