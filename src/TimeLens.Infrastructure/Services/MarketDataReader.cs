using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using System.Globalization;

namespace TimeLens.Infrastructure.Services;

public class MarketDataReader(
    IDatasetRepository datasetRepository,
    ITimeSeriesRepository timeSeriesRepository) : IMarketDataReader
{
    public async Task<MarketDataReadResult?> ReadAsync(
        string targetType,
        string targetId,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset? asOf = null,
        CancellationToken cancellationToken = default)
    {
        if (!targetType.Equals("dataset", StringComparison.OrdinalIgnoreCase)
            && !targetType.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!long.TryParse(targetId, NumberStyles.None, CultureInfo.InvariantCulture, out var seriesId))
        {
            return null;
        }

        var metadata = await datasetRepository.GetAsync(seriesId, cancellationToken)
            ?? (await datasetRepository.SearchAsync(new DatasetSearchFilter { SeriesId = seriesId }, cancellationToken)).FirstOrDefault();
        if (metadata is null)
        {
            return null;
        }

        var points = await timeSeriesRepository.GetSeriesAsync(metadata.SeriesId, start, end, asOf, cancellationToken, 10000);
        return new MarketDataReadResult(metadata, points);
    }
}
