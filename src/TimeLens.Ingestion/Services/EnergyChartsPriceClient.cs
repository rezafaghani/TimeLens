using System.Text.Json;

namespace TimeLens.Ingestion.Services;

public class EnergyChartsPriceClient(HttpClient httpClient)
{
    public async Task<JsonDocument> GetAsync(string biddingZone, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var startDate = start.UtcDateTime.ToString("yyyy-MM-dd");
        var endDate = end.UtcDateTime.ToString("yyyy-MM-dd");
        var uri = $"price?bzn={Uri.EscapeDataString(biddingZone)}&start={startDate}&end={endDate}";
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
