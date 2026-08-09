using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;

namespace TimeLens.Validation;

public class ValidationExecutionService(
    IDatasetRepository datasetRepository,
    ITimeSeriesRepository timeSeriesRepository,
    IQualityRepository qualityRepository)
{
    public async Task<ManualQualityEvaluationResult?> EvaluateDatasetAsync(
        string datasetId,
        DateTimeOffset start,
        DateTimeOffset end,
        DateTimeOffset? asOf,
        string? timeZone,
        HashSet<string>? validatorIds,
        CancellationToken cancellationToken,
        ManualQualityEvaluationRequest? request = null)
    {
        var metadata = await datasetRepository.GetAsync(datasetId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        if (!ExecutionPluginConfiguration.TryParseDuration(request?.Granularity ?? QualityValidationEngine.ToIsoDuration(metadata.Timeframe), out var granularity)
            || !TryParseOptionalDuration(request?.AllowedDelay, out var allowedDelay))
        {
            throw new ArgumentException("Granularity and allowedDelay must be ISO-8601 durations, for example PT15M.");
        }

        var points = await timeSeriesRepository.GetSeriesAsync(datasetId, start, end, asOf, cancellationToken, 10000);
        var findings = QualityValidationEngine.Evaluate(new QualityEvaluationRequest(
            metadata,
            start,
            end,
            DateTimeOffset.UtcNow,
            granularity,
            allowedDelay,
            request?.MinimumValue,
            request?.MaximumValue,
            request?.MaximumAbsoluteChange,
            request?.MaximumPercentageChange,
            request?.NearZeroFloor ?? 0.000001,
            request?.FlatLinePointCount ?? 0,
            points));

        if (validatorIds is not null)
        {
            findings = findings.Where(x => validatorIds.Contains(x.ValidatorId)).ToList();
        }

        var result = new ManualQualityEvaluationResult(metadata, start, end, points.Count, OverallStatus(findings), findings);
        var executionId = await qualityRepository.SaveManualEvaluationAsync(result, cancellationToken);
        return result with { ExecutionId = executionId };
    }

    private static string OverallStatus(List<QualityFindingDraftDto> findings)
    {
        if (findings.Any(x => x.QualityStatus == QualityStatuses.Critical))
        {
            return QualityStatuses.Critical;
        }

        return findings.Count == 0 ? QualityStatuses.Healthy : QualityStatuses.Degraded;
    }

    private static bool TryParseOptionalDuration(string? value, out TimeSpan? duration)
    {
        duration = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (!ExecutionPluginConfiguration.TryParseDuration(value, out var parsed))
        {
            return false;
        }

        duration = parsed;
        return true;
    }
}
