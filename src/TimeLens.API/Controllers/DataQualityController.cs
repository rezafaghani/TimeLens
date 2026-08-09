using System.Text.Json;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;
using Microsoft.AspNetCore.Mvc;

namespace TimeLens.API.Controllers;

[ApiController]
[Route("api/data-quality")]
public class DataQualityController(
    IQualityRepository qualityRepository,
    IValidationJobPublisher publisher) : ControllerBase
{
    [HttpGet("validation-types")]
    [ProducesResponseType(typeof(List<QualityValidatorTypeDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidationTypes(CancellationToken cancellationToken) =>
        Ok(await qualityRepository.GetValidatorTypesAsync(ValidationPluginUsage.Api, cancellationToken));

    [HttpGet("groups")]
    [ProducesResponseType(typeof(List<QualitySeriesGroupDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Groups(CancellationToken cancellationToken)
    {
        return Ok(await qualityRepository.GetSeriesGroupsAsync(cancellationToken));
    }

    [HttpPost("groups")]
    [ProducesResponseType(typeof(QualitySeriesGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpsertGroup([FromBody] UpsertQualitySeriesGroupRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest("A group name is required.");
        }

        return Ok(await qualityRepository.UpsertSeriesGroupAsync(request, cancellationToken));
    }

    [HttpPatch("groups/{id}/enabled")]
    [ProducesResponseType(typeof(QualitySeriesGroupDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetGroupEnabled([FromRoute] string id, [FromBody] SetEnabledRequest request, CancellationToken cancellationToken)
    {
        var group = await qualityRepository.SetSeriesGroupEnabledAsync(id, request.Enabled, cancellationToken);
        return group is null ? NotFound() : Ok(group);
    }

    [HttpGet("groups/{id}/members")]
    [ProducesResponseType(typeof(List<QualitySeriesGroupMemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GroupMembers([FromRoute] string id, CancellationToken cancellationToken)
    {
        return Ok(await qualityRepository.GetSeriesGroupMembersAsync(id, cancellationToken));
    }

    [HttpPut("groups/{id}/members")]
    [ProducesResponseType(typeof(List<QualitySeriesGroupMemberDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReplaceGroupMembers([FromRoute] string id, [FromBody] ReplaceQualitySeriesGroupMembersRequest request, CancellationToken cancellationToken)
    {
        return Ok(await qualityRepository.ReplaceSeriesGroupMembersAsync(id, request, cancellationToken));
    }

    [HttpGet("jobs")]
    [ProducesResponseType(typeof(List<QualityValidationJobDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Jobs([FromQuery] string? seriesId, CancellationToken cancellationToken)
    {
        var jobs = await qualityRepository.GetJobsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            jobs = jobs
                .Where(job => job.Targets.Any(target =>
                    (target.TargetType.Equals("series", StringComparison.OrdinalIgnoreCase)
                        || target.TargetType.Equals("dataset", StringComparison.OrdinalIgnoreCase))
                    && target.TargetId.Equals(seriesId, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return Ok(jobs);
    }

    [HttpGet("jobs/{id}")]
    [ProducesResponseType(typeof(QualityValidationJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Job([FromRoute] string id, CancellationToken cancellationToken)
    {
        var job = await qualityRepository.GetJobAsync(id, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpPost("jobs")]
    [ProducesResponseType(typeof(QualityValidationJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpsertJob([FromBody] UpsertQualityValidationJobRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.CronExpression)
            || string.IsNullOrWhiteSpace(request.WindowStartExpression)
            || string.IsNullOrWhiteSpace(request.WindowEndExpression)
            || request.Targets.Count == 0
            || request.Checks.Count == 0)
        {
            return BadRequest("A quality job requires a name, schedule, evaluation window, target, and validation check.");
        }

        var validatorIds = (await qualityRepository.GetValidatorTypesAsync(ValidationPluginUsage.Api, cancellationToken))
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (request.Checks.Any(x => !validatorIds.Contains(x.ValidatorId)))
        {
            return BadRequest("One or more validation checks are not supported.");
        }

        return Ok(await qualityRepository.UpsertJobAsync(request, cancellationToken));
    }

    [HttpPatch("jobs/{id}/enabled")]
    [ProducesResponseType(typeof(QualityValidationJobDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetJobEnabled([FromRoute] string id, [FromBody] SetEnabledRequest request, CancellationToken cancellationToken)
    {
        var job = await qualityRepository.SetJobEnabledAsync(id, request.Enabled, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpPost("jobs/{id}/runs")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RunJob([FromRoute] string id, [FromBody] RunQualityJobRequest request, CancellationToken cancellationToken)
    {
        var job = await qualityRepository.GetJobAsync(id, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        if (!DateTimeExpression.TryResolve(request.Start ?? job.WindowStartExpression, out var start, job.TimeZone)
            || !DateTimeExpression.TryResolve(request.End ?? job.WindowEndExpression, out var end, job.TimeZone)
            || start >= end)
        {
            return BadRequest("A valid half-open evaluation window is required.");
        }

        var validatorIds = job.Checks.Where(x => x.Enabled).Select(x => x.ValidatorId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (validatorIds.Count == 0)
        {
            return BadRequest("The quality job has no enabled validation checks.");
        }

        var message = new ValidationJobMessage
        {
            JobId = job.Id,
            ExecutionId = $"quality-execution-{Guid.NewGuid():N}",
            TriggerType = string.IsNullOrWhiteSpace(request.TriggerType) ? "manual" : request.TriggerType,
            WindowStartExpression = request.Start ?? job.WindowStartExpression,
            WindowEndExpression = request.End ?? job.WindowEndExpression
        };
        await publisher.PublishValidationAsync(message, cancellationToken);
        return Accepted(new { message.JobId, message.ExecutionId, message.TriggerType, Start = start, End = end });
    }

    [HttpGet("findings")]
    [ProducesResponseType(typeof(List<QualityFindingDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Findings(
        [FromQuery] string? datasetId,
        [FromQuery] string? seriesId,
        [FromQuery] bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        return Ok(await qualityRepository.GetFindingsAsync(datasetId, seriesId, activeOnly, cancellationToken));
    }

    [HttpGet("executions")]
    [ProducesResponseType(typeof(List<QualityExecutionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Executions(
        [FromQuery] string? jobId,
        [FromQuery] string? seriesId,
        CancellationToken cancellationToken = default)
    {
        return Ok(await qualityRepository.GetExecutionsAsync(jobId, seriesId, cancellationToken));
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(QualityStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Status(
        [FromQuery] string? datasetId,
        [FromQuery] string? seriesId,
        CancellationToken cancellationToken)
    {
        var status = await qualityRepository.GetStatusAsync(datasetId, seriesId, cancellationToken);
        return status is null
            ? Ok(new QualityStatusDto(datasetId ?? string.Empty, seriesId ?? string.Empty, QualityStatuses.Unknown, JsonSerializer.SerializeToElement(new { }), string.Empty, DateTimeOffset.UtcNow))
            : Ok(status);
    }

    [HttpGet("summary")]
    [ProducesResponseType(typeof(QualitySummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        return Ok(await qualityRepository.GetSummaryAsync(cancellationToken));
    }

    [HttpPost("evaluate")]
    [ProducesResponseType(typeof(ManualQualityEvaluationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Evaluate([FromBody] ManualQualityEvaluationRequest request, CancellationToken cancellationToken)
    {
        return BadRequest("Validation checks run in the validation worker. Create or run a validation job instead.");
    }
}

public record SetEnabledRequest(bool Enabled);
