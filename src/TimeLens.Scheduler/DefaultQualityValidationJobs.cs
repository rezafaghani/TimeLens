using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TimeLens.Domain.Entities;
using TimeLens.Domain.Models;

namespace TimeLens.Scheduler;

public static class DefaultQualityValidationJobs
{
    public static string JobIdFor(string seriesId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seriesId.ToLowerInvariant())));
        return $"quality-auto-{hash[..16].ToLowerInvariant()}";
    }

    public static UpsertQualityValidationJobRequest Create(IngestionSchedule schedule)
    {
        var configuration = JsonSerializer.SerializeToElement(new { });
        var freshnessConfiguration = JsonSerializer.SerializeToElement(new { gracePeriod = "PT24H" });
        var windowDays = Math.Max(1, (schedule.LookbackHours + 23) / 24);
        return new UpsertQualityValidationJobRequest(
            JobIdFor(schedule.SeriesId),
            $"Automatic data quality - {schedule.Name}",
            $"Automatically validates {schedule.SeriesId} after its first successful ingestion and daily thereafter.",
            true,
            "0 2 * * *",
            "UTC",
            $"today-{windowDays}",
            "today",
            4,
            300,
            null,
            [new UpsertQualityValidationJobTargetRequest("series", schedule.SeriesId, null)],
            [
                Check("metadata.required", configuration),
                Check("availability.empty-data", configuration),
                Check("completeness.missing-timestamps", configuration),
                Check("timestamps.alignment", configuration),
                Check("freshness.latest-point", freshnessConfiguration),
                Check("duplicates.timestamp-conflict", configuration),
                Check("validity.value-range", configuration)
            ]);
    }

    public static bool HasIngestedData(IEnumerable<IngestionExecution> executions) =>
        executions.Any(execution =>
            execution.Status == IngestionStatuses.Completed
            && execution.Inserted + execution.Skipped > 0);

    private static UpsertQualityValidationJobCheckRequest Check(string validatorId, JsonElement configuration) =>
        new(null, validatorId, null, true, configuration, null, null);
}
