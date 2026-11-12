using TimeLens.API.Controllers;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using Microsoft.AspNetCore.Mvc;

namespace TimeLens.UnitTest.Controllers;

public class DatasetsControllerTests
{
    [Fact]
    public async Task Series_RejectsEmptyRange()
    {
        var result = await new DatasetsController(Mock.Of<IDatasetRepository>(), Mock.Of<ITimeSeriesRepository>())
            .Series(1, "2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z", null, null, null, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Series_RejectsInvalidLimit()
    {
        var result = await new DatasetsController(Mock.Of<IDatasetRepository>(), Mock.Of<ITimeSeriesRepository>())
            .Series(1, "2026-01-01T00:00:00Z", "2026-01-01T01:00:00Z", null, null, 10001, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task SetDeprecated_UpdatesDatasetMetadata()
    {
        var repository = new Mock<IDatasetRepository>();
        var cancellationToken = CancellationToken.None;
        repository.Setup(x => x.SetDeprecatedAsync(1, true, cancellationToken))
            .ReturnsAsync(new DatasetMetadataDto { SeriesId = 1, Deprecated = true });

        var result = await new DatasetsController(repository.Object, Mock.Of<ITimeSeriesRepository>())
            .SetDeprecated(1, new SetDatasetDeprecatedRequest(true), cancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.True(Assert.IsType<DatasetMetadataDto>(ok.Value).Deprecated);
    }
}
