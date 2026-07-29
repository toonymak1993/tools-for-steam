using System.Text.Json;
using System.Threading.Channels;

namespace SteamLoader.App.Hosting;

public sealed class QuickAccessLiveUpdateHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Channel<string>> _subscribers = new();
    private long _nextSequence;

    public bool HasSubscribers
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count > 0;
            }
        }
    }

    public QuickAccessLiveUpdateSubscription Subscribe()
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscriberId = Guid.NewGuid();

        lock (_gate)
        {
            _subscribers[subscriberId] = channel;
        }

        channel.Writer.TryWrite(SerializeEvent("ready"));
        return new QuickAccessLiveUpdateSubscription(channel.Reader, () => RemoveSubscriber(subscriberId));
    }

    public void Publish(string topic)
    {
        PublishCore(topic, payload: null);
    }

    public void Publish(string topic, object? payload)
    {
        PublishCore(topic, payload);
    }

    private void PublishCore(string topic, object? payload)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return;
        }

        var serializedPayload = SerializeEvent(topic.Trim(), payload);
        KeyValuePair<Guid, Channel<string>>[] subscribers;

        lock (_gate)
        {
            subscribers = _subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            if (!subscriber.Value.Writer.TryWrite(serializedPayload))
            {
                RemoveSubscriber(subscriber.Key);
            }
        }
    }

    private string SerializeEvent(string topic, object? payload = null)
    {
        return JsonSerializer.Serialize(
            new QuickAccessLiveUpdateEvent(
                Topic: topic,
                Sequence: Interlocked.Increment(ref _nextSequence),
                SentAtUtc: DateTimeOffset.UtcNow,
                Payload: payload),
            JsonOptions);
    }

    private void RemoveSubscriber(Guid subscriberId)
    {
        Channel<string>? channel = null;

        lock (_gate)
        {
            if (_subscribers.Remove(subscriberId, out var removedChannel))
            {
                channel = removedChannel;
            }
        }

        channel?.Writer.TryComplete();
    }
}

public sealed class QuickAccessLiveUpdateSubscription : IDisposable
{
    private readonly Action _dispose;
    private int _disposed;

    internal QuickAccessLiveUpdateSubscription(ChannelReader<string> reader, Action dispose)
    {
        Reader = reader;
        _dispose = dispose;
    }

    public ChannelReader<string> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _dispose();
    }
}

public sealed record QuickAccessLiveUpdateEvent(
    string Topic,
    long Sequence,
    DateTimeOffset SentAtUtc,
    object? Payload = null);
