using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TimeLens.Ingestion.Services;

public class CoinbaseCandleClient(
    HttpClient httpClient,
    ProviderRateLimiter rateLimiter,
    IOptions<IngestionOptions> options,
    ILogger<CoinbaseCandleClient> logger)
{
    public async Task<JsonDocument> GetAsync(string productId, int granularitySeconds, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        var uri = $"products/{Uri.EscapeDataString(productId)}/candles?granularity={granularitySeconds}&start={Iso(start)}&end={Iso(end)}";
        var maxRetries = Math.Max(options.Value.MaxProviderRetries, 0);

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            await rateLimiter.WaitAsync(cancellationToken);

            using var response = await httpClient.GetAsync(uri, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt == maxRetries)
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            }

            var delay = GetRetryDelay(response, attempt);
            logger.LogWarning(
                "Coinbase returned 429 for {ProductId} {Granularity}. Retrying in {DelaySeconds:n0}s ({Attempt}/{MaxAttempts}).",
                productId,
                granularitySeconds,
                delay.TotalSeconds,
                attempt + 1,
                maxRetries);
            await Task.Delay(delay, cancellationToken);
        }

        throw new InvalidOperationException("Coinbase request retry loop exited unexpectedly.");
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } retryAfterDelta)
        {
            return retryAfterDelta;
        }

        if (response.Headers.RetryAfter?.Date is { } retryAfterDate)
        {
            var delay = retryAfterDate - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                return delay;
            }
        }

        var baseDelaySeconds = Math.Max(options.Value.RetryBaseDelaySeconds, 1);
        return TimeSpan.FromSeconds(baseDelaySeconds * Math.Pow(2, attempt));
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
