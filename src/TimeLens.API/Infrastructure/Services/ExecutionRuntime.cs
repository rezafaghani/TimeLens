using System.Text.Json;
using TimeLens.Domain.Interfaces;
using TimeLens.Domain.Models;
using TimeLens.Domain.Services;

namespace TimeLens.API.Infrastructure.Services;

public class ExecutionRuntime(
    ExecutionPluginRegistry registry,
    IMarketDataReader marketDataReader)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<List<ExecutionStepResultDto>> ExecuteAsync(
        ExecutionDefinitionDto definition,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        var plugins = definition.Plugins
            .Where(x => x.Enabled)
            .OrderBy(x => x.SortOrder)
            .ToList();
        var results = new List<ExecutionStepResultDto>();

        foreach (var target in definition.Targets)
        {
            var targetContext = await ResolveTargetAsync(target, start, end, cancellationToken);
            foreach (var definitionPlugin in plugins)
            {
                var plugin = registry.Resolve(definitionPlugin.PluginId)
                    ?? throw new ArgumentException($"Plugin '{definitionPlugin.PluginId}' is not available in this environment.");
                if (!plugin.Metadata.SupportedTargets.Contains(target.TargetType, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var context = targetContext with { Configuration = definitionPlugin.Configuration };
                results.AddRange(await plugin.ExecuteAsync(context, cancellationToken));
            }
        }

        return results;
    }

    private async Task<ExecutionPluginContext> ResolveTargetAsync(
        ExecutionDefinitionTargetDto target,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (!target.TargetType.Equals("dataset", StringComparison.OrdinalIgnoreCase)
            && !target.TargetType.Equals("series", StringComparison.OrdinalIgnoreCase))
        {
            return new ExecutionPluginContext(target.TargetType, target.TargetId, start, end, EmptyJson(), null, []);
        }

        var marketData = await marketDataReader.ReadAsync(target.TargetType, target.TargetId, start, end, null, cancellationToken)
            ?? throw new ArgumentException($"Market-data series '{target.TargetId}' was not found.");
        return new ExecutionPluginContext(target.TargetType, target.TargetId, start, end, EmptyJson(), marketData.Metadata, marketData.Points);
    }

    private static JsonElement EmptyJson() => JsonSerializer.SerializeToElement(new { }, JsonOptions);
}
