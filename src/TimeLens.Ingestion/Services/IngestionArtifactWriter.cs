using System.Security.Cryptography;
using System.Text.Json;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;

namespace TimeLens.Ingestion.Services;

public class IngestionArtifactWriter(IObjectStorage storage, IIngestionArtifactPublisher publisher)
{
    public async Task<RawIngestionArtifact> SaveRawAsync(
        IngestionJobMessage job,
        byte[] rawJson,
        int artifactCount,
        CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        var ingestedAt = DateTimeOffset.UtcNow;
        var objectKey = BuildObjectKey("raw", job.SeriesId, ingestedAt, id);
        await storage.PutAsync(objectKey, rawJson, "application/json", cancellationToken);

        return new RawIngestionArtifact
        {
            IngestionId = id,
            JobId = job.JobId,
            ExecutionId = job.ExecutionId,
            SeriesId = job.SeriesId,
            ProviderSeriesId = CanonicalSeriesRegistry.Get(job.SeriesId).ProviderSeriesId,
            IngestedAt = ingestedAt,
            RawObjectKey = objectKey,
            ArtifactCount = artifactCount
        };
    }

    public async Task SaveParsedAndPublishAsync(
        RawIngestionArtifact rawArtifact,
        long seriesId,
        string sourceMetadataVersion,
        IReadOnlyCollection<TimeSeriesWritePoint> points,
        CancellationToken cancellationToken)
    {
        if (seriesId != rawArtifact.SeriesId)
        {
            throw new InvalidOperationException(
                $"Parsed series ID {seriesId} does not match raw artifact series ID {rawArtifact.SeriesId}.");
        }

        var parsedArtifact = new ParsedDatasetArtifact
        {
            SeriesId = rawArtifact.SeriesId,
            Points = points.OrderBy(point => point.Timestamp).ToList()
        };
        var parsedJson = JsonSerializer.SerializeToUtf8Bytes(parsedArtifact);
        var parsedHash = Convert.ToHexString(SHA256.HashData(parsedJson)).ToLowerInvariant();
        var parsedObjectKey = BuildObjectKey("parsed", rawArtifact.SeriesId, rawArtifact.IngestedAt, rawArtifact.IngestionId);

        await storage.PutAsync(parsedObjectKey, parsedJson, "application/json", cancellationToken);
        await publisher.PublishAsync(new IngestionArtifactMessage
        {
            IngestionId = rawArtifact.IngestionId,
            JobId = rawArtifact.JobId,
            ExecutionId = rawArtifact.ExecutionId,
            SeriesId = rawArtifact.SeriesId,
            ProviderSeriesId = rawArtifact.ProviderSeriesId,
            IngestedAt = rawArtifact.IngestedAt,
            RawObjectKey = rawArtifact.RawObjectKey,
            ParsedObjectKey = parsedObjectKey,
            ParsedSha256 = parsedHash,
            SourceMetadataVersion = sourceMetadataVersion,
            PointCount = parsedArtifact.Points.Count,
            ArtifactCount = rawArtifact.ArtifactCount
        }, cancellationToken);
    }

    private static string BuildObjectKey(string type, long seriesId, DateTimeOffset ingestedAt, Guid id) =>
        $"{type}/{seriesId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{ingestedAt:yyyy/MM/dd}/{id:D}.json";
}
