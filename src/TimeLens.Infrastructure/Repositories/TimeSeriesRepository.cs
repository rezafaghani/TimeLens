using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;

namespace TimeLens.Infrastructure.Repositories;

public class TimeSeriesRepository(TimeLensContext context, IMarketDataUpdatePublisher? marketDataUpdatePublisher = null) : ITimeSeriesRepository
{
    private const string BarsTable = "market_ohlcv_bars";

    public async Task<TimeSeriesInsertResult> InsertBatchAsync(TimeSeriesBatchRequest request, CancellationToken cancellationToken = default)
    {
        var insertTime = DateTimeOffset.UtcNow;
        var result = new TimeSeriesInsertResult { AsOf = insertTime };

        await using var connection = context.CreateClickHouseConnection();
        await connection.OpenAsync(cancellationToken);

        foreach (var point in request.Points.OrderBy(x => x.Timestamp))
        {
            var latest = await GetLatestAsync(connection, request.DatasetId, point.Timestamp, cancellationToken);
            if (latest is not null
                && latest.Open == point.Open
                && latest.High == point.High
                && latest.Low == point.Low
                && latest.Close == point.Close
                && latest.Volume == point.Volume)
            {
                result.Skipped++;
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $$"""
                INSERT INTO {{BarsTable}} (
                    series_id, timestamp, open, high, low, close, volume,
                    as_of, inserted_at, source_metadata_version)
                VALUES (
                    {seriesId:String}, {timestamp:DateTime64(3)}, {open:Float64}, {high:Float64},
                    {low:Float64}, {close:Float64}, {volume:Float64},
                    {asOf:DateTime64(3)}, {insertedAt:DateTime64(3)}, {sourceMetadataVersion:String})
                """;
            command.AddParameter("seriesId", request.DatasetId);
            command.AddParameter("timestamp", DbValue.Utc(point.Timestamp));
            command.AddParameter("open", point.Open);
            command.AddParameter("high", point.High);
            command.AddParameter("low", point.Low);
            command.AddParameter("close", point.Close);
            command.AddParameter("volume", point.Volume);
            command.AddParameter("asOf", DbValue.Utc(insertTime));
            command.AddParameter("insertedAt", DbValue.Utc(insertTime));
            command.AddParameter("sourceMetadataVersion", request.SourceMetadataVersion);
            await command.ExecuteNonQueryAsync(cancellationToken);

            result.Inserted++;
            if (marketDataUpdatePublisher is not null)
            {
                await marketDataUpdatePublisher.PublishAsync(request.DatasetId, point, insertTime, "upsert", cancellationToken);
            }
        }

        return result;
    }

    public async Task<List<TimeSeriesPointDto>> GetSeriesAsync(
        string datasetId,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset? asOf,
        CancellationToken cancellationToken = default,
        int? limit = null)
    {
        await using var connection = context.CreateClickHouseConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT timestamp, open, high, low, close, volume, as_of
            FROM (
                SELECT
                    timestamp,
                    open,
                    high,
                    low,
                    close,
                    volume,
                    as_of,
                    row_number() OVER (PARTITION BY series_id, timestamp ORDER BY as_of DESC, inserted_at DESC) AS rn
                FROM {{BarsTable}}
                WHERE series_id = {seriesId:String}
                  AND timestamp >= {start:DateTime64(3)}
                  AND timestamp < {end:DateTime64(3)}
                  {{(asOf.HasValue ? "AND as_of <= {asOf:DateTime64(3)}" : "")}}
            )
            WHERE rn = 1
            ORDER BY timestamp
            {{(limit.HasValue ? "LIMIT {limit:Int32}" : "")}}
            """;
        command.AddParameter("seriesId", datasetId);
        command.AddParameter("start", DbValue.Utc(start));
        command.AddParameter("end", DbValue.Utc(end));
        if (asOf.HasValue)
        {
            command.AddParameter("asOf", DbValue.Utc(asOf.Value));
        }
        if (limit.HasValue)
        {
            command.AddParameter("limit", limit.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TimeSeriesPointDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TimeSeriesPointDto
            {
                Timestamp = new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero),
                Open = reader.GetDouble(1),
                High = reader.GetDouble(2),
                Low = reader.GetDouble(3),
                Close = reader.GetDouble(4),
                Volume = reader.GetDouble(5),
                AsOf = new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero)
            });
        }

        return result;
    }

    private static async Task<TimeSeriesWritePoint?> GetLatestAsync(System.Data.Common.DbConnection connection, string seriesId, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT open, high, low, close, volume
            FROM {{BarsTable}}
            WHERE series_id = {seriesId:String} AND timestamp = {timestamp:DateTime64(3)}
            ORDER BY as_of DESC, inserted_at DESC
            LIMIT 1
            """;
        command.AddParameter("seriesId", seriesId);
        command.AddParameter("timestamp", DbValue.Utc(timestamp));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TimeSeriesWritePoint
            {
                Timestamp = timestamp,
                Open = reader.GetDouble(0),
                High = reader.GetDouble(1),
                Low = reader.GetDouble(2),
                Close = reader.GetDouble(3),
                Volume = reader.GetDouble(4)
            }
            : null;
    }
}
