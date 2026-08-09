using TimeLens.Contracts;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using Grpc.Core;

namespace TimeLens.Insert.Services;

public class IngestionWriteGrpcService(ITimeSeriesRepository timeSeriesRepository) : IngestionWrite.IngestionWriteBase
{
    public override async Task<TimeSeriesInsertResultMessage> InsertTimeSeriesBatch(TimeSeriesBatchMessage request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.DatasetId) || request.Points.Count == 0)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "DatasetId and at least one point are required."));
        }

        var result = await timeSeriesRepository.InsertBatchAsync(new TimeSeriesBatchRequest
        {
            DatasetId = request.DatasetId,
            SourceMetadataVersion = request.SourceMetadataVersion,
            Points = request.Points.Select(x => new TimeSeriesWritePoint
            {
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(x.UnixSeconds),
                Open = x.Open,
                High = x.High,
                Low = x.Low,
                Close = x.Close,
                Volume = x.Volume
            }).ToList()
        }, context.CancellationToken);

        return new TimeSeriesInsertResultMessage
        {
            Inserted = result.Inserted,
            Skipped = result.Skipped,
            AsOfUnixSeconds = result.AsOf.ToUnixTimeSeconds()
        };
    }
}
