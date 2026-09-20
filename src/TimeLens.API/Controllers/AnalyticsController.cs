using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;

namespace TimeLens.API.Controllers;

[ApiController]
[Route("api/analytics")]
public class AnalyticsController(ITimeSeriesRepository timeSeriesRepository) : ControllerBase
{
    [HttpGet("correlation")]
    [ProducesResponseType(typeof(MarketCorrelationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Correlation(
        [FromQuery] string leftDatasetId,
        [FromQuery] string rightDatasetId,
        [FromQuery] string start,
        [FromQuery] string end,
        [FromQuery] string bucket = "1h",
        [FromQuery] int maxLag = 24,
        [FromQuery] string? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(leftDatasetId)
            || string.IsNullOrWhiteSpace(rightDatasetId)
            || !DateTimeExpression.TryResolve(start, out var startTime, timeZone)
            || !DateTimeExpression.TryResolve(end, out var endTime, timeZone)
            || startTime >= endTime
            || !TryParseBucket(bucket, out var bucketSize)
            || maxLag is < 0 or > 168)
        {
            return BadRequest("Valid dataset ids, date range, bucket (for example 1h or 1d), and maxLag from 0 to 168 are required.");
        }

        var leftTask = timeSeriesRepository.GetSeriesAsync(leftDatasetId, startTime, endTime, null, cancellationToken, 10000);
        var rightTask = timeSeriesRepository.GetSeriesAsync(rightDatasetId, startTime, endTime, null, cancellationToken, 10000);
        await Task.WhenAll(leftTask, rightTask);

        return Ok(MarketCorrelation.Calculate(
            leftDatasetId,
            await leftTask,
            rightDatasetId,
            await rightTask,
            bucketSize,
            maxLag));
    }

    private static bool TryParseBucket(string value, out TimeSpan bucket)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length < 2
            || !double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            || !double.IsFinite(amount))
        {
            bucket = TimeSpan.Zero;
            return false;
        }

        var suffix = value[^1];
        bucket = suffix switch
        {
            'm' when amount is >= 1 and <= 43200 => TimeSpan.FromMinutes(amount),
            'h' when amount is >= 1 and <= 720 => TimeSpan.FromHours(amount),
            'd' when amount is >= 1 and <= 30 => TimeSpan.FromDays(amount),
            _ => TimeSpan.Zero
        };
        return bucket >= TimeSpan.FromMinutes(1) && bucket <= TimeSpan.FromDays(30);
    }
}
