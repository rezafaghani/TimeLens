using System.Globalization;
using System.Text.Json;

namespace TimeLens.Ingestion.Services;

public class CoinMetricsClient(HttpClient httpClient)
{
    public async Task<JsonDocument> GetAsync(string asset, string metric, string frequency, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var startDate = start.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endDate = end.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var uri = $"timeseries/asset-metrics?assets={Uri.EscapeDataString(asset)}&metrics={Uri.EscapeDataString(metric)}&frequency={Uri.EscapeDataString(frequency)}&start_time={startDate}&end_time={endDate}&page_size=10000";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Coin Metrics returned {(int)response.StatusCode} ({response.ReasonPhrase}): {detail}", null, response.StatusCode);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
