using Microsoft.Extensions.Options;

namespace TimeLens.Ingestion.Services;

public class ProviderRateLimiter(IOptions<IngestionOptions> options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(options.Value.MinRequestIntervalSeconds, 0));
            var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
            if (elapsed < interval)
            {
                await Task.Delay(interval - elapsed, cancellationToken);
            }

            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}
