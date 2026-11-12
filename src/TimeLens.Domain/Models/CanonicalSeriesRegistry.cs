namespace TimeLens.Domain.Models;

public sealed record CanonicalSeriesDefinition(
    long SeriesId,
    string Provider,
    string ProviderSeriesId,
    string Timeframe);

public static class CanonicalSeriesRegistry
{
    // These IDs are global and persisted; never reassign or reuse an existing value.
    private static readonly IReadOnlyDictionary<long, CanonicalSeriesDefinition> ById =
        new CanonicalSeriesDefinition[]
        {
            new(1, "energy-charts", "energy-charts:dk1:day-ahead-price", "1h"),
            new(2, "energy-charts", "energy-charts:dk1:day-ahead-price", "15m"),
            new(3, "energy-charts", "energy-charts:dk2:day-ahead-price", "1h"),
            new(4, "energy-charts", "energy-charts:dk2:day-ahead-price", "15m"),
            new(5, "coinbase-exchange", "coinbase:btc-usd:1m", "1m"),
            new(6, "coinbase-exchange", "coinbase:btc-usd:5m", "5m"),
            new(7, "coinbase-exchange", "coinbase:btc-usd:15m", "15m"),
            new(8, "coinbase-exchange", "coinbase:btc-usd:1h", "1h"),
            new(9, "coinbase-exchange", "coinbase:btc-usd:1d", "1d"),
            new(10, "coinbase-exchange", "coinbase:eth-usd:1m", "1m"),
            new(11, "coinbase-exchange", "coinbase:eth-usd:5m", "5m"),
            new(12, "coinbase-exchange", "coinbase:eth-usd:15m", "15m"),
            new(13, "coinbase-exchange", "coinbase:eth-usd:1h", "1h"),
            new(14, "coinbase-exchange", "coinbase:eth-usd:1d", "1d"),
            new(15, "coin-metrics-community", "coinmetrics:btc:hashrate:1d", "1d"),
            new(16, "coin-metrics-community", "coinmetrics:btc:adractcnt:1d", "1d"),
            new(17, "coin-metrics-community", "coinmetrics:btc:txcnt:1d", "1d")
        }.ToDictionary(definition => definition.SeriesId);

    private static readonly IReadOnlyDictionary<string, CanonicalSeriesDefinition> ByDefinition =
        ById.Values.ToDictionary(
            definition => MakeKey(definition.Provider, definition.ProviderSeriesId, definition.Timeframe),
            StringComparer.Ordinal);

    public static IReadOnlyCollection<CanonicalSeriesDefinition> All => ById.Values.ToArray();

    public static CanonicalSeriesDefinition Get(long seriesId) =>
        ById.TryGetValue(seriesId, out var definition)
            ? definition
            : throw new InvalidOperationException($"Series ID {seriesId} is not registered.");

    public static CanonicalSeriesDefinition Resolve(string provider, string providerSeriesId, string timeframe) =>
        ByDefinition.TryGetValue(MakeKey(provider, providerSeriesId, timeframe), out var definition)
            ? definition
            : throw new InvalidOperationException(
                $"Series definition '{provider}/{providerSeriesId}/{timeframe}' is not registered.");

    public static CanonicalSeriesDefinition ResolveSchedule(
        string provider,
        IReadOnlyDictionary<string, string> parameters)
    {
        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var timeframe = normalizedProvider switch
        {
            "coinbase-exchange" => Required(parameters, "timeframe"),
            "energy-charts" => Required(parameters, "timeframe", "1h"),
            "coin-metrics-community" => Required(parameters, "frequency", "1d"),
            _ => throw new InvalidOperationException($"Provider '{provider}' has no registered series definitions.")
        };
        var providerSeriesId = normalizedProvider switch
        {
            "coinbase-exchange" => $"coinbase:{Required(parameters, "product_id")}:{timeframe}",
            "energy-charts" => $"energy-charts:{Required(parameters, "bzn")}:day-ahead-price",
            "coin-metrics-community" =>
                $"coinmetrics:{Required(parameters, "asset", "btc")}:{Required(parameters, "metric")}:{timeframe}",
            _ => throw new InvalidOperationException($"Provider '{provider}' has no registered series definitions.")
        };
        return Resolve(normalizedProvider, providerSeriesId, timeframe);
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key, string? fallback = null) =>
        parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback ?? throw new InvalidOperationException($"Schedule parameter '{key}' is required.");

    private static string MakeKey(string provider, string providerSeriesId, string timeframe) =>
        $"{provider.Trim().ToLowerInvariant()}\0{providerSeriesId.Trim().ToLowerInvariant()}\0{timeframe.Trim().ToLowerInvariant()}";
}
