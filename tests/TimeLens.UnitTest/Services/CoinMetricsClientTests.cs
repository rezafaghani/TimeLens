using System.Net;
using TimeLens.Ingestion.Services;

namespace TimeLens.UnitTest.Services;

public class CoinMetricsClientTests
{
    [Fact]
    public async Task GetAsync_UsesSupportedDailyDateFormat()
    {
        Uri? requestedUri = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[]}") };
        })) { BaseAddress = new Uri("https://community-api.coinmetrics.io/v4/") };

        using var result = await new CoinMetricsClient(httpClient).GetAsync(
            "btc",
            "HashRate",
            "1d",
            DateTimeOffset.Parse("2026-08-20T12:34:56.1234567Z"),
            DateTimeOffset.Parse("2026-09-19T14:40:31.7654321Z"),
            CancellationToken.None);

        Assert.Contains("start_time=2026-08-20&end_time=2026-09-19", requestedUri!.Query);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
