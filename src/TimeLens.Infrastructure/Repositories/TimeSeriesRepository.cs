using ClickHouse.Client.Copy;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Observability;

namespace TimeLens.Infrastructure.Repositories;

public class TimeSeriesRepository(TimeLensContext context, IMarketDataUpdatePublisher? marketDataUpdatePublisher = null) : ITimeSeriesRepository
{
    private const string BarsTable = "market_ohlcv_bars";

    public async Task<TimeSeriesInsertResult> InsertBatchAsync(TimeSeriesBatchRequest request, CancellationToken cancellationToken = default)
    {
        using var activity = TimeLensTelemetry.ActivitySource.StartActivity("ClickHouse InsertBatch");
        activity?.SetTag("db.system.name", "clickhouse");
        activity?.SetTag("db.operation.name", "insert");
        activity?.SetTag("db.collection.name", BarsTable);
        activity?.SetTag("timelens.series_id", request.SeriesId);
        activity?.SetTag("timelens.record_count", request.Points.Count);
        var insertTime = DateTimeOffset.UtcNow;
        var result = new TimeSeriesInsertResult { AsOf = insertTime };
        var points = request.Points.OrderBy(point => point.Timestamp).ToArray();
        if (points.Length == 0)
        {
            return result;
        }

        await using var connection = context.CreateClickHouseConnection();
        await connection.OpenAsync(cancellationToken);

        var latestByTimestamp = await GetLatestAsync(
            connection,
            request.SeriesId,
            points[0].Timestamp,
            points[^1].Timestamp,
            cancellationToken);
        var pointsToInsert = new List<TimeSeriesWritePoint>(points.Length);
        foreach (var point in points)
        {
            var timestampKey = GetTimestampKey(point.Timestamp);
            if (latestByTimestamp.TryGetValue(timestampKey, out var latest)
                && latest.Open == point.Open
                && latest.High == point.High
                && latest.Low == point.Low
                && latest.Close == point.Close
                && latest.Volume == point.Volume)
            {
                result.Skipped++;
                continue;
            }

            pointsToInsert.Add(point);
            latestByTimestamp[timestampKey] = point;
        }

        if (pointsToInsert.Count > 0)
        {
            using var bulkCopy = new ClickHouseBulkCopy(connection)
            {
                DestinationTableName = $"{connection.Database}.{BarsTable}",
                ColumnNames =
                [
                    "series_id",
                    "timestamp",
                    "open",
                    "high",
                    "low",
                    "close",
                    "volume",
                    "as_of",
                    "inserted_at",
                    "source_metadata_version"
                ],
                BatchSize = 10_000,
                MaxDegreeOfParallelism = 1
            };
            await bulkCopy.InitAsync();
            await bulkCopy.WriteToServerAsync(pointsToInsert.Select(point => new object[]
            {
                request.SeriesId,
                DbValue.Utc(point.Timestamp),
                point.Open,
                point.High,
                point.Low,
                point.Close,
                point.Volume,
                DbValue.Utc(insertTime),
                DbValue.Utc(insertTime),
                request.SourceMetadataVersion
            }));

            result.Inserted = pointsToInsert.Count;
            if (marketDataUpdatePublisher is not null)
            {
                await marketDataUpdatePublisher.PublishBatchAsync(
                    request.SeriesId,
                    pointsToInsert,
                    insertTime,
                    "upsert",
                    cancellationToken);
            }
        }

        activity?.SetTag("timelens.records_inserted", result.Inserted);
        activity?.SetTag("timelens.records_skipped", result.Skipped);
        return result;
    }

    public async Task<List<TimeSeriesPointDto>> GetSeriesAsync(
        long seriesId,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset? asOf,
        CancellationToken cancellationToken = default,
        int? limit = null)
    {
        using var activity = TimeLensTelemetry.ActivitySource.StartActivity("ClickHouse ReadSeries");
        activity?.SetTag("db.system.name", "clickhouse");
        activity?.SetTag("db.operation.name", "select");
        activity?.SetTag("db.collection.name", BarsTable);
        activity?.SetTag("timelens.series_id", seriesId);
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
                WHERE series_id = {seriesId:Int64}
                  AND timestamp >= {start:DateTime64(3)}
                  AND timestamp < {end:DateTime64(3)}
                  {{(asOf.HasValue ? "AND as_of <= {asOf:DateTime64(3)}" : "")}}
            )
            WHERE rn = 1
            ORDER BY timestamp
            {{(limit.HasValue ? "LIMIT {limit:Int32}" : "")}}
            """;
        command.AddParameter("seriesId", seriesId);
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

    private static async Task<Dictionary<long, TimeSeriesWritePoint>> GetLatestAsync(
        System.Data.Common.DbConnection connection,
        long seriesId,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, TimeSeriesWritePoint>();
        await using var command = connection.CreateCommand();
        command.CommandText = $$"""
            SELECT timestamp, open, high, low, close, volume
            FROM {{BarsTable}}
            WHERE series_id = {seriesId:Int64}
              AND timestamp >= {start:DateTime64(3)}
              AND timestamp <= {end:DateTime64(3)}
            ORDER BY timestamp, as_of DESC, inserted_at DESC
            LIMIT 1 BY timestamp
            """;
        command.AddParameter("seriesId", seriesId);
        command.AddParameter("start", DbValue.Utc(start));
        command.AddParameter("end", DbValue.Utc(end));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var timestamp = new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero);
            result[GetTimestampKey(timestamp)] = new TimeSeriesWritePoint
            {
                Timestamp = timestamp,
                Open = reader.GetDouble(1),
                High = reader.GetDouble(2),
                Low = reader.GetDouble(3),
                Close = reader.GetDouble(4),
                Volume = reader.GetDouble(5)
            };
        }

        return result;
    }

    private static long GetTimestampKey(DateTimeOffset timestamp) => timestamp.ToUnixTimeMilliseconds();
}
