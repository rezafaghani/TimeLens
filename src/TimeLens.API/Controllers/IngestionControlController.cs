using TimeLens.Domain.Entities;
using TimeLens.Domain.Models;
using TimeLens.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace TimeLens.API.Controllers;

[ApiController]
[Route("api/ingestion")]
public class IngestionControlController(
    IIngestionControlRepository repository,
    IDatasetRepository datasets,
    RabbitMqJobPublisher publisher) : ControllerBase
{
    [HttpGet("schedules")]
    [ProducesResponseType(typeof(List<IngestionSchedule>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Schedules([FromQuery] string? seriesId, CancellationToken cancellationToken)
    {
        return Ok(await repository.GetSchedulesAsync(seriesId, cancellationToken));
    }

    [HttpGet("jobs")]
    [ProducesResponseType(typeof(List<IngestionJob>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Jobs(
        [FromQuery] string? scheduleId,
        [FromQuery] string? seriesId,
        CancellationToken cancellationToken)
    {
        return Ok(await repository.GetJobsAsync(scheduleId, seriesId, cancellationToken));
    }

    [HttpGet("executions")]
    [ProducesResponseType(typeof(List<IngestionExecution>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Executions(
        [FromQuery] string? jobId,
        [FromQuery] string? scheduleId,
        [FromQuery] string? seriesId,
        CancellationToken cancellationToken)
    {
        return Ok(await repository.GetExecutionsAsync(jobId, scheduleId, seriesId, cancellationToken));
    }

    [HttpGet("series/{seriesId}/schedules")]
    [ProducesResponseType(typeof(List<IngestionSchedule>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SeriesSchedules([FromRoute] string seriesId, CancellationToken cancellationToken)
    {
        return Ok(await repository.GetSchedulesAsync(seriesId, cancellationToken));
    }

    [HttpGet("series/{seriesId}/jobs")]
    [ProducesResponseType(typeof(List<IngestionJob>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SeriesJobs([FromRoute] string seriesId, CancellationToken cancellationToken)
    {
        return Ok(await repository.GetJobsAsync(null, seriesId, cancellationToken));
    }

    [HttpGet("series/{seriesId}/executions")]
    [ProducesResponseType(typeof(List<IngestionExecution>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SeriesExecutions([FromRoute] string seriesId, CancellationToken cancellationToken)
    {
        return Ok(await repository.GetExecutionsAsync(null, null, seriesId, cancellationToken));
    }

    [HttpPost("schedules")]
    [ProducesResponseType(typeof(IngestionSchedule), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSchedule([FromBody] CreateIngestionScheduleRequest request, CancellationToken cancellationToken)
    {
        var validation = ValidateScheduleRequest(new UpdateIngestionScheduleRequest(
            request.CronExpression,
            request.Enabled,
            request.WindowStartExpression,
            request.WindowEndExpression,
            request.BatchSize));
        if (validation is not null)
        {
            return BadRequest(validation);
        }

        if (string.IsNullOrWhiteSpace(request.SeriesId) || string.IsNullOrWhiteSpace(request.Source) || string.IsNullOrWhiteSpace(request.Endpoint))
        {
            return BadRequest("Series, source, and provider route are required.");
        }

        var schedule = await repository.CreateScheduleAsync(new IngestionSchedule
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? request.SeriesId.Trim() : request.Name.Trim(),
            SeriesId = request.SeriesId.Trim(),
            CronExpression = request.CronExpression.Trim(),
            DefaultCronExpression = request.CronExpression.Trim(),
            Enabled = request.Enabled,
            Source = request.Source.Trim(),
            Endpoint = request.Endpoint.Trim(),
            Parameters = request.Parameters ?? [],
            LookbackHours = request.LookbackHours < 1 ? 48 : request.LookbackHours,
            WindowStartExpression = request.WindowStartExpression.Trim(),
            WindowEndExpression = request.WindowEndExpression.Trim(),
            DefaultWindowStartExpression = request.WindowStartExpression.Trim(),
            DefaultWindowEndExpression = request.WindowEndExpression.Trim(),
            BatchSize = request.BatchSize
        }, cancellationToken);

        return CreatedAtAction(nameof(Schedules), new { seriesId = schedule.SeriesId }, schedule);
    }

    [HttpPut("schedules/{id}")]
    [ProducesResponseType(typeof(IngestionSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSchedule([FromRoute] string id, [FromBody] UpdateIngestionScheduleRequest request, CancellationToken cancellationToken)
    {
        var schedule = await repository.GetScheduleAsync(id, cancellationToken);
        if (schedule is null)
        {
            return NotFound();
        }

        var validation = ValidateScheduleRequest(request);
        if (validation is not null)
        {
            return BadRequest(validation);
        }

        schedule.CronExpression = request.CronExpression.Trim();
        schedule.Enabled = request.Enabled;
        schedule.WindowStartExpression = request.WindowStartExpression.Trim();
        schedule.WindowEndExpression = request.WindowEndExpression.Trim();
        schedule.BatchSize = request.BatchSize;

        return Ok(await repository.UpdateScheduleAsync(schedule, cancellationToken));
    }

    [HttpPost("schedules/{id}/reset")]
    [ProducesResponseType(typeof(IngestionSchedule), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ResetSchedule([FromRoute] string id, CancellationToken cancellationToken)
    {
        var schedule = await repository.ResetScheduleAsync(id, cancellationToken);
        return schedule is null ? NotFound() : Ok(schedule);
    }

    [HttpPost("schedules/{id}/backloads")]
    [ProducesResponseType(typeof(IngestionJobMessage), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateBackload([FromRoute] string id, [FromBody] CreateBackloadRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DatasetId))
        {
            return BadRequest("Dataset is required.");
        }

        if (string.IsNullOrWhiteSpace(request.WindowStartExpression) || string.IsNullOrWhiteSpace(request.WindowEndExpression))
        {
            return BadRequest("Start and end expressions are required.");
        }

        if (request.BatchSize < 1)
        {
            return BadRequest("Batch size must be at least 1.");
        }

        if (!DateTimeExpression.TryResolve(request.WindowStartExpression, out _) || !DateTimeExpression.TryResolve(request.WindowEndExpression, out _))
        {
            return BadRequest("Use now, today, today-1, today+1, now-48h, or an ISO timestamp for the period.");
        }

        var schedule = await repository.GetScheduleAsync(id, cancellationToken);
        if (schedule is null)
        {
            return NotFound();
        }

        var metadata = await datasets.GetAsync(request.DatasetId, cancellationToken);
        if (metadata is null || metadata.SeriesId != schedule.SeriesId)
        {
            return BadRequest("Dataset does not belong to this schedule series.");
        }

        var message = await repository.CreateBackloadJobAsync(
            schedule,
            metadata.Endpoint,
            metadata.Provider,
            WithoutDateRange(metadata.RequestParameters),
            request.WindowStartExpression.Trim(),
            request.WindowEndExpression.Trim(),
            request.BatchSize,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await publisher.PublishAsync(message, cancellationToken);

        return Accepted(message);
    }

    private static string? ValidateScheduleRequest(UpdateIngestionScheduleRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CronExpression))
        {
            return "Cron expression is required.";
        }

        if (!LooksLikeFiveFieldCron(request.CronExpression))
        {
            return "Cron expression is invalid.";
        }

        if (string.IsNullOrWhiteSpace(request.WindowStartExpression) || string.IsNullOrWhiteSpace(request.WindowEndExpression))
        {
            return "Window start and end expressions are required.";
        }

        if (!DateTimeExpression.TryResolve(request.WindowStartExpression, out _) || !DateTimeExpression.TryResolve(request.WindowEndExpression, out _))
        {
            return "Use now, today, today-1, today+1, now-48h, or an ISO timestamp for the window.";
        }

        return request.BatchSize < 1 ? "Batch size must be at least 1." : null;
    }

    private static bool LooksLikeFiveFieldCron(string value)
    {
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length == 5;
    }

    private static Dictionary<string, string> WithoutDateRange(Dictionary<string, string> parameters)
    {
        var copy = new Dictionary<string, string>(parameters);
        copy.Remove("start");
        copy.Remove("end");
        return copy;
    }
}

public record UpdateIngestionScheduleRequest(
    string CronExpression,
    bool Enabled,
    string WindowStartExpression,
    string WindowEndExpression,
    int BatchSize);

public record CreateIngestionScheduleRequest(
    string Name,
    string SeriesId,
    string Source,
    string Endpoint,
    Dictionary<string, string> Parameters,
    string CronExpression,
    bool Enabled,
    int LookbackHours,
    string WindowStartExpression,
    string WindowEndExpression,
    int BatchSize);

public record CreateBackloadRequest(
    string DatasetId,
    string WindowStartExpression,
    string WindowEndExpression,
    int BatchSize);
