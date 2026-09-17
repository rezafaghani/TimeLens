using System.Text.Json;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;
using TimeLens.Validation;
using Orleans;

namespace TimeLens.Validation.Worker.Grains;

public class ValidationCheckGrain(
    IEnumerable<IExecutionPlugin> plugins,
    IMarketDataReader marketDataReader) : Grain, IValidationCheckGrain
{
    public async Task<string> ValidateAsync(ValidationCheckMessage message)
    {
        var plugin = plugins.SingleOrDefault(x =>
            x.Metadata.Id.Equals(message.ValidatorId, StringComparison.OrdinalIgnoreCase)
            || x.Metadata.Id.Equals($"{QualityValidationPlugin.IdPrefix}{message.ValidatorId}", StringComparison.OrdinalIgnoreCase));
        if (plugin is null)
        {
            throw new ArgumentException($"Validation plugin '{message.ValidatorId}' is not available in this worker.");
        }

        var marketData = await marketDataReader.ReadAsync(message.TargetType, message.TargetId, message.Start, message.End);
        if (marketData is null)
        {
            return "[]";
        }

        var context = new ExecutionPluginContext(
            message.TargetType,
            message.TargetId,
            message.Start,
            message.End,
            ParseConfiguration(message.ConfigurationJson),
            marketData.Metadata,
            marketData.Points);
        var results = await plugin.ExecuteAsync(context);
        return JsonSerializer.Serialize(results);
    }

    private static JsonElement ParseConfiguration(string configurationJson)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(configurationJson) ? "{}" : configurationJson);
        return document.RootElement.Clone();
    }
}
