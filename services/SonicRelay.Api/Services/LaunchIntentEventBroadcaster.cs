using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SonicRelay.Api.Services;

/// <summary>A committed launch-intent change the bot should react to.</summary>
public sealed record LaunchIntentEvent(long Sequence, string Type, Guid IntentId);

public static class LaunchIntentEventTypes
{
    public const string SessionReady = "session_ready";
}

/// <summary>
/// In-process fan-out of launch-intent events to SSE subscribers. Each subscriber owns a bounded
/// buffer; a subscriber that falls behind is disconnected (its channel completes) instead of
/// silently losing events, so its client reconnects and reconciles through
/// <c>GET /api/launch-intents/pending</c>.
/// </summary>
/// <remarks>
/// Single-instance by design: events published on one API replica reach only subscribers
/// connected to that replica. Running several replicas requires a backplane or relying on the
/// bot's reconciliation; see docs/discord-activity-launch-contract.md.
/// </remarks>
public sealed class LaunchIntentEventBroadcaster
{
    public const int DefaultBufferCapacity = 64;

    private readonly ConcurrentDictionary<long, Subscription> _subscribers = new();
    private readonly int _bufferCapacity;
    private long _nextSubscriptionId;
    private long _nextSequence;

    public LaunchIntentEventBroadcaster() : this(DefaultBufferCapacity)
    {
    }

    public LaunchIntentEventBroadcaster(int bufferCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bufferCapacity, 1);
        _bufferCapacity = bufferCapacity;
    }

    public int SubscriberCount => _subscribers.Count;

    /// <summary>Registers a subscriber. Events published after this call returns are delivered.</summary>
    public ILaunchIntentSubscription Subscribe()
    {
        var id = Interlocked.Increment(ref _nextSubscriptionId);
        var channel = Channel.CreateBounded<LaunchIntentEvent>(new BoundedChannelOptions(_bufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        var subscription = new Subscription(this, id, channel);
        _subscribers[id] = subscription;
        return subscription;
    }

    /// <summary>Publishes an event. Call only after the state change has been committed.</summary>
    public void Publish(string type, Guid intentId)
    {
        var item = new LaunchIntentEvent(Interlocked.Increment(ref _nextSequence), type, intentId);
        foreach (var subscription in _subscribers.Values)
        {
            if (!subscription.Channel.Writer.TryWrite(item))
                subscription.Overflow();
        }
    }

    private void Remove(long id) => _subscribers.TryRemove(id, out _);

    private sealed class Subscription(
        LaunchIntentEventBroadcaster owner, long id, Channel<LaunchIntentEvent> channel) : ILaunchIntentSubscription
    {
        public Channel<LaunchIntentEvent> Channel { get; } = channel;
        public ChannelReader<LaunchIntentEvent> Reader => Channel.Reader;
        public bool Overflowed { get; private set; }

        public void Overflow()
        {
            Overflowed = true;
            owner.Remove(id);
            Channel.Writer.TryComplete();
        }

        public ValueTask DisposeAsync()
        {
            owner.Remove(id);
            Channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

public interface ILaunchIntentSubscription : IAsyncDisposable
{
    ChannelReader<LaunchIntentEvent> Reader { get; }

    /// <summary>True when the subscriber was dropped because its buffer filled up.</summary>
    bool Overflowed { get; }
}
