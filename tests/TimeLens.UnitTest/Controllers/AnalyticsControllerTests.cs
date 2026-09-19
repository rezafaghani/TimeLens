using Microsoft.AspNetCore.Mvc;
using TimeLens.API.Controllers;
using TimeLens.Domain.Interfaces;

namespace TimeLens.UnitTest.Controllers;

public class AnalyticsControllerTests
{
    [Fact]
    public async Task Correlation_RejectsInvalidBucket()
    {
        var result = await new AnalyticsController(Mock.Of<ITimeSeriesRepository>()).Correlation(
            "crypto",
            "energy",
            "2026-01-01T00:00:00Z",
            "2026-01-02T00:00:00Z",
            "invalid",
            24,
            null,
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }
}
