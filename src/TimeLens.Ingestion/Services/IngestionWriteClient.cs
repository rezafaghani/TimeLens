using TimeLens.Contracts;
using TimeLens.Domain.Models;

namespace TimeLens.Ingestion.Services;

public class IngestionWriteClient(IngestionWrite.IngestionWriteClient client)
{
    public async Task<TimeSeriesInsertResult> InsertBatchAsync(TimeSeriesBatchRequest batch, CancellationToken cancellationToken)
    {
        var request = new TimeSeriesBatchMessage
        {
            DatasetId = batch.DatasetId,
            SourceMetadataVersion = batch.SourceMetadataVersion
        };
        request.Points.AddRange(batch.Points.Select(x => new TimeSeriesWritePointMessage
        {
            UnixSeconds = x.Timestamp.ToUnixTimeSeconds(),
            Open = x.Open,
            High = x.High,
            Low = x.Low,
            Close = x.Close,
            Volume = x.Volume
        }));

        var response = await client.InsertTimeSeriesBatchAsync(request, cancellationToken: cancellationToken);
        return new TimeSeriesInsertResult
        {
            Inserted = response.Inserted,
            Skipped = response.Skipped,
            AsOf = DateTimeOffset.FromUnixTimeSeconds(response.AsOfUnixSeconds)
        };
    }
}
