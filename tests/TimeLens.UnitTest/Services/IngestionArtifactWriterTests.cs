using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Ingestion.Services;

namespace TimeLens.UnitTest.Services;

public class IngestionArtifactWriterTests
{
    [Fact]
    public async Task SavesRawAndParsedArtifactsBeforePublishingTheirMetadata()
    {
        var storage = new Mock<IObjectStorage>();
        var publisher = new Mock<IIngestionArtifactPublisher>();
        var rawJson = """{"values":[1,2,3]}"""u8.ToArray();
        byte[]? parsedJson = null;
        IngestionArtifactMessage? published = null;
        var cancellationToken = CancellationToken.None;
        var sequence = new MockSequence();

        storage.InSequence(sequence)
            .Setup(x => x.PutAsync(
                It.Is<string>(key => key.StartsWith("raw/")),
                It.Is<byte[]>(content => content.SequenceEqual(rawJson)),
                "application/json",
                cancellationToken))
            .Returns(Task.CompletedTask);
        storage.InSequence(sequence)
            .Setup(x => x.PutAsync(
                It.Is<string>(key => key.StartsWith("parsed/")),
                It.IsAny<byte[]>(),
                "application/json",
                cancellationToken))
            .Callback<string, byte[], string, CancellationToken>((_, content, _, _) => parsedJson = content)
            .Returns(Task.CompletedTask);
        publisher.InSequence(sequence)
            .Setup(x => x.PublishAsync(It.IsAny<IngestionArtifactMessage>(), cancellationToken))
            .Callback<IngestionArtifactMessage, CancellationToken>((message, _) => published = message)
            .Returns(Task.CompletedTask);

        var writer = new IngestionArtifactWriter(storage.Object, publisher.Object);
        var job = new IngestionJobMessage
        {
            JobId = "job-1",
            ExecutionId = "execution-1",
            SeriesId = 5
        };
        var point = new TimeSeriesWritePoint
        {
            Timestamp = DateTimeOffset.Parse("2025-01-01T00:00:00Z"),
            Open = 1,
            High = 2,
            Low = 0.5,
            Close = 1.5,
            Volume = 3
        };

        var rawArtifact = await writer.SaveRawAsync(job, rawJson, 1, cancellationToken);
        await writer.SaveParsedAndPublishAsync(rawArtifact, 5, "metadata-v1", [point], cancellationToken);

        Assert.NotNull(parsedJson);
        Assert.NotNull(published);
        Assert.StartsWith("raw/5/", rawArtifact.RawObjectKey);
        Assert.StartsWith("parsed/5/", published.ParsedObjectKey);
        Assert.Equal(rawArtifact.IngestionId, published.IngestionId);
        Assert.Equal(rawArtifact.IngestedAt, published.IngestedAt);
        Assert.Equal(5, published.SeriesId);
        Assert.Equal("coinbase:btc-usd:1m", published.ProviderSeriesId);
        using (var messageDocument = JsonDocument.Parse(JsonSerializer.Serialize(published, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
        {
            Assert.Equal(JsonValueKind.Number, messageDocument.RootElement.GetProperty("seriesId").ValueKind);
            Assert.False(messageDocument.RootElement.TryGetProperty("datasetId", out _));
        }
        Assert.Equal("metadata-v1", published.SourceMetadataVersion);
        Assert.Equal(1, published.PointCount);
        Assert.Equal(1, published.ArtifactCount);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(parsedJson)).ToLowerInvariant(),
            published.ParsedSha256);

        var parsed = JsonSerializer.Deserialize<ParsedDatasetArtifact>(parsedJson);
        Assert.NotNull(parsed);
        Assert.Equal(5, parsed.SeriesId);
        Assert.Equal(point.Timestamp, Assert.Single(parsed.Points).Timestamp);
    }
}
