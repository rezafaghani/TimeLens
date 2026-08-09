using TimeLens.API.Controllers;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace TimeLens.UnitTest.Controllers;

public class DataQualityControllerTests
{
    [Fact]
    public async Task ValidationTypes_ReturnsRegisteredValidators()
    {
        var qualityRepository = new Mock<IQualityRepository>();
        qualityRepository.Setup(x => x.GetValidatorTypesAsync(ValidationPluginUsage.Api, CancellationToken.None))
            .ReturnsAsync(Validators());

        var result = await Controller(qualityRepository.Object).ValidationTypes(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var validators = Assert.IsType<List<QualityValidatorTypeDto>>(ok.Value);
        Assert.Contains(validators, x => x.Id == "completeness.missing-timestamps");
        Assert.Contains(validators, x => x.Id == "freshness.latest-point");
    }

    [Fact]
    public async Task UpsertJob_RejectsUnknownValidator()
    {
        var request = new UpsertQualityValidationJobRequest(
            null,
            "Job",
            null,
            true,
            "*/15 * * * *",
            "UTC",
            "now-24h",
            "now",
            null,
            null,
            null,
            [new UpsertQualityValidationJobTargetRequest("dataset", "dataset-1", null)],
            [new UpsertQualityValidationJobCheckRequest(null, "unknown.validator", null, true, null, null, null)]);

        var qualityRepository = new Mock<IQualityRepository>();
        qualityRepository.Setup(x => x.GetValidatorTypesAsync(ValidationPluginUsage.Api, CancellationToken.None))
            .ReturnsAsync(Validators());

        var result = await Controller(qualityRepository.Object).UpsertJob(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Evaluate_DoesNotRunChecksInApi()
    {
        var result = await Controller()
            .Evaluate(new ManualQualityEvaluationRequest(
                "dataset-1",
                "2026-01-01T10:00:00Z",
                "2026-01-01T11:00:00Z",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Status_ReturnsUnknownWhenNoSnapshotExists()
    {
        var qualityRepository = new Mock<IQualityRepository>();
        qualityRepository.Setup(x => x.GetStatusAsync(null, "coinbase:btc-usd:1m", CancellationToken.None))
            .ReturnsAsync((QualityStatusDto?)null);

        var result = await Controller(qualityRepository.Object).Status(null, "coinbase:btc-usd:1m", CancellationToken.None);

        var status = Assert.IsType<QualityStatusDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(QualityStatuses.Unknown, status.OverallStatus);
        Assert.Equal("coinbase:btc-usd:1m", status.SeriesId);
    }

    [Fact]
    public async Task RunJob_PublishesValidationMessage()
    {
        var qualityRepository = new Mock<IQualityRepository>();
        qualityRepository.Setup(x => x.GetJobAsync("job-1", CancellationToken.None))
            .ReturnsAsync(new QualityValidationJobDto(
                "job-1",
                "Job",
                "",
                true,
                "*/15 * * * *",
                "UTC",
                "2026-01-01T10:00:00Z",
                "2026-01-01T11:00:00Z",
                4,
                300,
                default,
                [new QualityValidationJobTargetDto("dataset", "dataset-1", default)],
                [new QualityValidationJobCheckDto("check-1", "completeness.missing-timestamps", 1, true, default, default, 0)],
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                null));
        var publisher = new Mock<IValidationJobPublisher>();

        var result = await Controller(qualityRepository.Object, publisher.Object)
            .RunJob("job-1", new RunQualityJobRequest(null, null), CancellationToken.None);

        Assert.IsType<AcceptedResult>(result);
        publisher.Verify(x => x.PublishValidationAsync(It.Is<ValidationJobMessage>(message =>
            message.JobId == "job-1"
            && message.TriggerType == "manual"
            && message.WindowStartExpression == "2026-01-01T10:00:00Z"
            && message.WindowEndExpression == "2026-01-01T11:00:00Z"), CancellationToken.None), Times.Once);
    }

    private static DataQualityController Controller(
        IQualityRepository? qualityRepository = null,
        IValidationJobPublisher? publisher = null) =>
        new(qualityRepository ?? Mock.Of<IQualityRepository>(),
            publisher ?? Mock.Of<IValidationJobPublisher>());

    private static List<QualityValidatorTypeDto> Validators() =>
    [
        new("completeness.missing-timestamps", "completeness", "Missing timestamps", "", "series", 1, "warning", JsonSerializer.SerializeToElement(new { })),
        new("freshness.latest-point", "freshness", "Latest point freshness", "", "series", 1, "critical", JsonSerializer.SerializeToElement(new { }))
    ];
}
