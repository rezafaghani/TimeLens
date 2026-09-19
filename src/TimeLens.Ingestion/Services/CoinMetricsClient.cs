using System.Globalization;
using System.Text.Json;

namespace TimeLens.Ingestion.Services;

public class CoinMetricsClient(HttpClient httpClient)
{
    public async Task<JsonDocument> GetAsync(string asset, string metric, string frequency, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var uri = $"timeseries/asset-metrics?assets={Uri.EscapeDataString(asset)}&metrics={Uri.EscapeDataString(metric)}&frequency={Uri.EscapeDataString(frequency)}&start_time={Uri.EscapeDataString(start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}&end_time={Uri.EscapeDataString(end.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))}&page_size=10000";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
