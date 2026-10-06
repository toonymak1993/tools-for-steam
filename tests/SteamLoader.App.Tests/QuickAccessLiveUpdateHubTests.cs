using SteamLoader.App.Hosting;
using Xunit;

namespace SteamLoader.App.Tests;

public sealed class QuickAccessLiveUpdateHubTests
{
    [Fact]
    public void TopicSubscriptionOnlyReceivesRequestedEvents()
    {
        var hub = new QuickAccessLiveUpdateHub();
        using var subscription = hub.Subscribe(["processes.state"]);

        Assert.True(subscription.Reader.TryRead(out var ready));
        Assert.Contains("\"topic\":\"ready\"", ready, StringComparison.Ordinal);
        Assert.True(hub.HasSubscribersFor("processes.state"));
        Assert.False(hub.HasSubscribersFor("smart-home.state"));

        hub.Publish("smart-home.state", new { value = 1 });
        Assert.False(subscription.Reader.TryRead(out _));

        hub.Publish("processes.state", new { value = 2 });
        Assert.True(subscription.Reader.TryRead(out var update));
        Assert.Contains("\"topic\":\"processes.state\"", update, StringComparison.Ordinal);
    }

    [Fact]
    public void SubscriptionWithoutTopicsRemainsBackwardCompatible()
    {
        var hub = new QuickAccessLiveUpdateHub();
        using var subscription = hub.Subscribe();
        Assert.True(subscription.Reader.TryRead(out _));

        Assert.True(hub.HasSubscribersFor("any.topic"));
        hub.Publish("any.topic");
        Assert.True(subscription.Reader.TryRead(out var update));
        Assert.Contains("\"topic\":\"any.topic\"", update, StringComparison.Ordinal);
    }
}
