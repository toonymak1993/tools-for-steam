using System.Text.Json;
using System.Threading.Channels;

namespace SteamLoader.App.Hosting;

public sealed class QuickAccessLiveUpdateHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Subscriber> _subscribers = new();
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

    public bool HasSubscribersFor(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            return false;
        }

        var normalizedTopic = topic.Trim();
        lock (_gate)
        {
            return _subscribers.Values.Any(subscriber => subscriber.Accepts(normalizedTopic));
        }
    }

    public QuickAccessLiveUpdateSubscription Subscribe(IEnumerable<string>? topics = null)
    {
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscriberId = Guid.NewGuid();
        var normalizedTopics = topics?
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Select(topic => topic.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            _subscribers[subscriberId] = new Subscriber(
                channel,
                normalizedTopics is { Count: > 0 } ? normalizedTopics : null);
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

        var normalizedTopic = topic.Trim();
        var serializedPayload = SerializeEvent(normalizedTopic, payload);
        KeyValuePair<Guid, Subscriber>[] subscribers;

        lock (_gate)
        {
            subscribers = _subscribers.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            if (!subscriber.Value.Accepts(normalizedTopic))
            {
                continue;
            }

            if (!subscriber.Value.Channel.Writer.TryWrite(serializedPayload))
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
            if (_subscribers.Remove(subscriberId, out var removedSubscriber))
            {
                channel = removedSubscriber.Channel;
            }
        }

        channel?.Writer.TryComplete();
    }

    private sealed record Subscriber(Channel<string> Channel, HashSet<string>? Topics)
    {
        public bool Accepts(string topic) =>
            Topics is null || Topics.Contains(topic) || Topics.Contains("*");
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
