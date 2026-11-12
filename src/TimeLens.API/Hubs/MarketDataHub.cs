using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using TimeLens.API.Infrastructure.Services;
using TimeLens.Domain.Observability;

namespace TimeLens.API.Hubs;

public class MarketDataHub(ILogger<MarketDataHub> logger) : Hub
{
    public async Task Subscribe(MarketDataSubscription subscription)
    {
        var group = MarketDataLiveGroups.For(subscription.SeriesId);

        using var activity = TimeLensTelemetry.ActivitySource.StartActivity("LiveUpdateSubscribe");
        activity?.SetTag("signalr.connection_id", Context.ConnectionId);
        activity?.SetTag("market.subscription_group", group);
        activity?.SetTag("market.series_id", subscription.SeriesId);

        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        logger.LogInformation("SignalR connection {ConnectionId} subscribed to {Group}.", Context.ConnectionId, group);
    }

    public async Task Unsubscribe(MarketDataSubscription subscription)
    {
        var group = MarketDataLiveGroups.For(subscription.SeriesId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
        logger.LogInformation("SignalR connection {ConnectionId} unsubscribed from {Group}.", Context.ConnectionId, group);
    }
}

public record MarketDataSubscription(
    long SeriesId);
