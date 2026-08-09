using TimeLens.Domain.Entities;
using TimeLens.Domain.Interfaces;
using TimeLens.API.Controllers;
using TimeLens.Domain.Models;
using TimeLens.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace TimeLens.UnitTest.Controllers;

public class IngestionControlControllerTests
{
    [Fact]
    public async Task SeriesEndpoints_FilterBySeriesId()
    {
        var repository = new Mock<IIngestionControlRepository>();
        var cancellationToken = CancellationToken.None;
        var seriesId = "coinbase:btc-usd:1m";

        repository.Setup(x => x.GetSchedulesAsync(seriesId, cancellationToken))
            .ReturnsAsync([new IngestionSchedule { SeriesId = seriesId }]);
        repository.Setup(x => x.GetJobsAsync(null, seriesId, cancellationToken))
            .ReturnsAsync([new IngestionJob { SeriesId = seriesId }]);
        repository.Setup(x => x.GetExecutionsAsync(null, null, seriesId, cancellationToken))
            .ReturnsAsync([new IngestionExecution { SeriesId = seriesId }]);

        var controller = CreateController(repository);

        Assert.Single(Assert.IsType<OkObjectResult>(await controller.SeriesSchedules(seriesId, cancellationToken)).Value as List<IngestionSchedule> ?? []);
        Assert.Single(Assert.IsType<OkObjectResult>(await controller.SeriesJobs(seriesId, cancellationToken)).Value as List<IngestionJob> ?? []);
        Assert.Single(Assert.IsType<OkObjectResult>(await controller.SeriesExecutions(seriesId, cancellationToken)).Value as List<IngestionExecution> ?? []);

        repository.Verify(x => x.GetSchedulesAsync(seriesId, cancellationToken), Times.Once);
        repository.Verify(x => x.GetJobsAsync(null, seriesId, cancellationToken), Times.Once);
        repository.Verify(x => x.GetExecutionsAsync(null, null, seriesId, cancellationToken), Times.Once);
    }

    [Fact]
    public async Task UpdateSchedule_RejectsInvalidCron()
    {
        var repository = new Mock<IIngestionControlRepository>();
        var cancellationToken = CancellationToken.None;
        repository.Setup(x => x.GetScheduleAsync("schedule-1", cancellationToken))
            .ReturnsAsync(new IngestionSchedule { Id = "schedule-1" });

        var result = await CreateController(repository).UpdateSchedule(
            "schedule-1",
            new UpdateIngestionScheduleRequest("not-a-cron", true, "today-1", "today", 500),
            cancellationToken);

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(x => x.UpdateScheduleAsync(It.IsAny<IngestionSchedule>(), cancellationToken), Times.Never);
    }

    [Fact]
    public async Task UpdateSchedule_SavesWindowAndCron()
    {
        var repository = new Mock<IIngestionControlRepository>();
        var cancellationToken = CancellationToken.None;
        repository.Setup(x => x.GetScheduleAsync("schedule-1", cancellationToken))
            .ReturnsAsync(new IngestionSchedule { Id = "schedule-1" });
        repository.Setup(x => x.UpdateScheduleAsync(It.IsAny<IngestionSchedule>(), cancellationToken))
            .ReturnsAsync((IngestionSchedule schedule, CancellationToken _) => schedule);

        var result = await CreateController(repository).UpdateSchedule(
            "schedule-1",
            new UpdateIngestionScheduleRequest("*/15 * * * *", true, "today-1", "today+1", 250),
            cancellationToken);

        Assert.IsType<OkObjectResult>(result);
        repository.Verify(x => x.UpdateScheduleAsync(
            It.Is<IngestionSchedule>(schedule =>
                schedule.CronExpression == "*/15 * * * *"
                && schedule.WindowStartExpression == "today-1"
                && schedule.WindowEndExpression == "today+1"
                && schedule.BatchSize == 250),
            cancellationToken), Times.Once);
    }

    [Fact]
    public async Task CreateSchedule_CreatesManualSchedule()
    {
        var repository = new Mock<IIngestionControlRepository>();
        var cancellationToken = CancellationToken.None;
        repository.Setup(x => x.CreateScheduleAsync(It.IsAny<IngestionSchedule>(), cancellationToken))
            .ReturnsAsync((IngestionSchedule schedule, CancellationToken _) => schedule);

        var result = await CreateController(repository).CreateSchedule(
            new CreateIngestionScheduleRequest(
                "BTC-USD 1m",
                "coinbase:btc-usd:1m",
                "coinbase-exchange",
                "products/candles",
                new Dictionary<string, string> { ["product_id"] = "BTC-USD", ["timeframe"] = "1m" },
                "*/30 * * * *",
                true,
                48,
                "now-48h",
                "now",
                500),
            cancellationToken);

        Assert.IsType<CreatedAtActionResult>(result);
        repository.Verify(x => x.CreateScheduleAsync(
            It.Is<IngestionSchedule>(schedule =>
                schedule.SeriesId == "coinbase:btc-usd:1m"
                && schedule.Source == "coinbase-exchange"
                && schedule.Endpoint == "products/candles"
                && schedule.Enabled),
            cancellationToken), Times.Once);
    }

    private static IngestionControlController CreateController(Mock<IIngestionControlRepository> repository)
    {
        var datasets = new Mock<IDatasetRepository>();
        var publisher = new RabbitMqJobPublisher(Options.Create(new RabbitMqOptions()));
        return new IngestionControlController(repository.Object, datasets.Object, publisher);
    }
}
