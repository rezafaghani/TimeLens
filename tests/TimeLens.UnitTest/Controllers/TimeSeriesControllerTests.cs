using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Insert.Controllers;
using Microsoft.AspNetCore.Mvc;

namespace TimeLens.UnitTest.Controllers;

public class TimeSeriesControllerTests
{
    [Fact]
    public async Task InsertBatch_EmptyRequest_ReturnsBadRequest()
    {
        var repository = new Mock<ITimeSeriesRepository>();
        var controller = new TimeSeriesController(repository.Object);

        var result = await controller.InsertBatch(new TimeSeriesBatchRequest(), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        repository.Verify(x => x.InsertBatchAsync(It.IsAny<TimeSeriesBatchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InsertBatch_ValidRequest_WritesTimeSeries()
    {
        var request = new TimeSeriesBatchRequest
        {
            DatasetId = "dataset-1",
            Points =
            [
                new TimeSeriesWritePoint
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Open = 42,
                    High = 42,
                    Low = 42,
                    Close = 42,
                    Volume = 10
                }
            ]
        };
        var repository = new Mock<ITimeSeriesRepository>();
        repository.Setup(x => x.InsertBatchAsync(request, CancellationToken.None))
            .ReturnsAsync(new TimeSeriesInsertResult { Inserted = 1 });
        var controller = new TimeSeriesController(repository.Object);

        var result = await controller.InsertBatch(request, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(1, Assert.IsType<TimeSeriesInsertResult>(ok.Value).Inserted);
        repository.Verify(x => x.InsertBatchAsync(request, CancellationToken.None), Times.Once);
    }
}
