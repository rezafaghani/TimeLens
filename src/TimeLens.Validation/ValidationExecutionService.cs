using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;

namespace TimeLens.Validation;

public class ValidationExecutionService(
    IMarketDataReader marketDataReader,
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
        var marketData = await marketDataReader.ReadAsync("series", datasetId, start, end, asOf, cancellationToken);
        if (marketData is null)
        {
            return null;
        }

        if (!ExecutionPluginConfiguration.TryParseDuration(request?.Granularity ?? QualityValidationEngine.ToIsoDuration(marketData.Metadata.Timeframe), out var granularity)
            || !TryParseOptionalDuration(request?.AllowedDelay, out var allowedDelay))
        {
            throw new ArgumentException("Granularity and allowedDelay must be ISO-8601 durations, for example PT15M.");
        }

        var findings = QualityValidationEngine.Evaluate(new QualityEvaluationRequest(
            marketData.Metadata,
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
            marketData.Points));

        if (validatorIds is not null)
        {
            findings = findings.Where(x => validatorIds.Contains(x.ValidatorId)).ToList();
        }

        var result = new ManualQualityEvaluationResult(marketData.Metadata, start, end, marketData.Points.Count, OverallStatus(findings), findings);
        var executionId = await qualityRepository.SaveManualEvaluationAsync(result, cancellationToken);
        return result with { ExecutionId = executionId };
    }

    private static string OverallStatus(List<QualityFindingDraftDto> findings)
    {
        if (findings.Any(x => x.QualityStatus == QualityStatuses.Critical))
        {
            return QualityStatuses.Critical;
        }

        return findings.Count == 0 ? QualityStatuses.Healthy : QualityStatuses.Warning;
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
