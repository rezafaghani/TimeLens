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
        [FromQuery] long leftSeriesId,
        [FromQuery] long rightSeriesId,
        [FromQuery] string start,
        [FromQuery] string end,
        [FromQuery] string bucket = "1h",
        [FromQuery] int maxLag = 24,
        [FromQuery] string? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        if (leftSeriesId <= 0
            || rightSeriesId <= 0
            || !DateTimeExpression.TryResolve(start, out var startTime, timeZone)
            || !DateTimeExpression.TryResolve(end, out var endTime, timeZone)
            || startTime >= endTime
            || !TryParseBucket(bucket, out var bucketSize)
            || maxLag is < 0 or > 168)
        {
            return BadRequest("Valid numeric series IDs, date range, bucket (for example 1h or 1d), and maxLag from 0 to 168 are required.");
        }

        var leftTask = timeSeriesRepository.GetSeriesAsync(leftSeriesId, startTime, endTime, null, cancellationToken, 10000);
        var rightTask = timeSeriesRepository.GetSeriesAsync(rightSeriesId, startTime, endTime, null, cancellationToken, 10000);
        await Task.WhenAll(leftTask, rightTask);

        return Ok(MarketCorrelation.Calculate(
            leftSeriesId,
            await leftTask,
            rightSeriesId,
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
