using TimeLens.Domain.Entities;
using TimeLens.Scheduler;

namespace TimeLens.UnitTest.Services;

public class DefaultQualityValidationJobsTests
{
    [Fact]
    public void Create_UsesSeriesTargetAndDoesNotAssumeEnergyPricesArePositive()
    {
        var schedule = DefaultSchedules.Create()
            .Single(schedule => schedule.Source == "energy-charts" && schedule.SeriesId.EndsWith(":dk1:day-ahead-price"));

        var job = DefaultQualityValidationJobs.Create(schedule);

        Assert.Equal(DefaultQualityValidationJobs.JobIdFor(schedule.SeriesId), job.Id);
        Assert.Equal("series", Assert.Single(job.Targets).TargetType);
        Assert.Equal(schedule.SeriesId, job.Targets[0].TargetId);
        Assert.Equal("0 2 * * *", job.CronExpression);
        Assert.Equal("today-30", job.WindowStartExpression);
        Assert.Equal("today", job.WindowEndExpression);
        Assert.Equal("PT24H", job.Checks.Single(check => check.ValidatorId == "freshness.latest-point")
            .Configuration!.Value.GetProperty("gracePeriod").GetString());
        Assert.DoesNotContain(job.Checks, check => check.ValidatorId == "validity.price-positive");
    }

    [Fact]
    public void Create_UsesTheScheduleLookbackForShorterSeries()
    {
        var schedule = DefaultSchedules.Create()
            .Single(schedule => schedule.Source == "coinbase-exchange" && schedule.SeriesId.EndsWith(":btc-usd:1m"));

        var job = DefaultQualityValidationJobs.Create(schedule);

        Assert.Equal("today-2", job.WindowStartExpression);
    }

    [Theory]
    [InlineData("queued", 10, 0, false)]
    [InlineData("failed", 10, 0, false)]
    [InlineData("completed", 0, 0, false)]
    [InlineData("completed", 0, 10, true)]
    public void HasIngestedData_RequiresSuccessfulExecutionWithStoredPoints(
        string status,
        int inserted,
        int skipped,
        bool expected)
    {
        var executions = new[]
        {
            new IngestionExecution
            {
                Status = status,
                Inserted = inserted,
                Skipped = skipped
            }
        };

        Assert.Equal(expected, DefaultQualityValidationJobs.HasIngestedData(executions));
    }
}
