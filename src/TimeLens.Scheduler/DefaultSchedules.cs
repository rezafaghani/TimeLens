using TimeLens.Domain.Entities;

namespace TimeLens.Scheduler;

public static class DefaultSchedules
{
    public static IReadOnlyCollection<IngestionSchedule> Create()
    {
        var schedules = new List<IngestionSchedule>();
        foreach (var productId in new[] { "BTC-USD", "ETH-USD" })
        {
            foreach (var timeframe in new[] { "1m", "5m", "15m", "1h", "1d" })
            {
                var seriesId = $"coinbase:{productId}:{timeframe}".ToLowerInvariant();
                schedules.Add(new IngestionSchedule
                {
                    Id = $"coinbase-{productId.ToLowerInvariant()}-{timeframe}",
                    Name = $"{productId} {timeframe}",
                    SeriesId = seriesId,
                    Source = "coinbase-exchange",
                    Endpoint = "products/candles",
                    Parameters = new Dictionary<string, string>
                    {
                        ["product_id"] = productId,
                        ["timeframe"] = timeframe
                    },
                    CronExpression = Cron(timeframe),
                    DefaultCronExpression = Cron(timeframe),
                    LookbackHours = timeframe == "1d" ? 24 * 30 : 48,
                    WindowStartExpression = timeframe == "1d" ? "now-720h" : "now-48h",
                    WindowEndExpression = "now",
                    DefaultWindowStartExpression = timeframe == "1d" ? "now-720h" : "now-48h",
                    DefaultWindowEndExpression = "now",
                    BatchSize = 500
                });
            }
        }

        foreach (var biddingZone in new[] { "DK1", "DK2" })
        {
            schedules.Add(new IngestionSchedule
            {
                Id = $"energy-charts-{biddingZone.ToLowerInvariant()}-day-ahead-price",
                Name = $"{biddingZone} day-ahead electricity price",
                SeriesId = $"energy-charts:{biddingZone}:day-ahead-price".ToLowerInvariant(),
                Source = "energy-charts",
                Endpoint = "price",
                Parameters = new Dictionary<string, string>
                {
                    ["bzn"] = biddingZone,
                    ["timeframe"] = "1h"
                },
                CronExpression = "15 * * * *",
                DefaultCronExpression = "15 * * * *",
                LookbackHours = 48,
                WindowStartExpression = "now-48h",
                WindowEndExpression = "now+24h",
                DefaultWindowStartExpression = "now-48h",
                DefaultWindowEndExpression = "now+24h",
                BatchSize = 500
            });
        }

        return schedules;
    }

    private static string Cron(string timeframe) => timeframe switch
    {
        "1m" => "*/5 * * * *",
        "5m" => "*/10 * * * *",
        "15m" => "*/15 * * * *",
        "1h" => "5 * * * *",
        "1d" => "10 0 * * *",
        _ => "*/30 * * * *"
    };
}
