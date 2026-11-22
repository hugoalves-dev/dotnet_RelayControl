using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SonicRelay.Api.Services;
using SonicRelay.Domain.Devices;
using Xunit;

namespace SonicRelay.Api.IntegrationTests;

public sealed class LaunchIntentEventsTests
{
    private const string ServiceToken = "integration-test-discord-service-token";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Events_stream_rejects_anonymous_wrong_token_and_device_callers()
    {
        using var factory = new SonicRelayApiFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/launch-intents/events")).StatusCode);

        using var wrong = factory.CreateClient();
        wrong.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-the-service-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync("/api/launch-intents/events")).StatusCode);

        using var device = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            device, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        var deviceResponse = await device.GetAsync("/api/launch-intents/events");
        Assert.Contains(deviceResponse.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Equal(0, factory.Services.GetRequiredService<LaunchIntentEventBroadcaster>().SubscriberCount);
    }

    [Fact]
    public async Task Completing_a_share_emits_one_session_ready_after_connected()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["LaunchIntents:EventsHeartbeatSeconds"] = "1"
        });
        using var bot = CreateBot(factory);
        await using var stream = await SseReader.OpenAsync(bot);

        Assert.Equal("text/event-stream", stream.ContentType);
        var connected = await stream.NextAsync();
        Assert.Equal("connected", connected.Type);
        Assert.Equal("5000", connected.Retry);

        var (intentId, sessionId, publisher) = await CreateConsumedShareAsync(factory, bot);
        var complete = await publisher.PostAsJsonAsync($"/api/launch-intents/share/{intentId}/complete", new { sessionId });
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        // An idempotent retry must not emit a second notification.
        var retry = await publisher.PostAsJsonAsync($"/api/launch-intents/share/{intentId}/complete", new { sessionId });
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        // A rejected completion must not emit either.
        var rejected = await publisher.PostAsJsonAsync($"/api/launch-intents/share/{Guid.NewGuid()}/complete", new { sessionId });
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);

        var ready = await stream.NextAsync();
        Assert.Equal("session_ready", ready.Type);
        Assert.False(string.IsNullOrEmpty(ready.Id));
        var payload = JsonDocument.Parse(ready.Data).RootElement;
        Assert.Equal(intentId, payload.GetProperty("intentId").GetGuid());
        Assert.Single(payload.EnumerateObject()); // no watch URLs or capabilities on the wire

        // The bot then fetches authoritative details and acknowledges through the existing routes.
        var status = await bot.GetFromJsonAsync<JsonElement>($"/api/launch-intents/{intentId}?watchTtlSeconds=0");
        Assert.Equal("session_ready", status.GetProperty("status").GetString());
        var published = await bot.PostAsJsonAsync($"/api/launch-intents/{intentId}/published", new { messageId = "m-1" });
        Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);

        // Retry and rejected completions queued nothing: the next frame is an idle heartbeat.
        Assert.Equal("heartbeat", (await stream.NextAsync()).Type);
    }

    [Fact]
    public async Task Stream_sends_heartbeats_while_idle()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["LaunchIntents:EventsHeartbeatSeconds"] = "1"
        });
        using var bot = CreateBot(factory);
        await using var stream = await SseReader.OpenAsync(bot);

        Assert.Equal("connected", (await stream.NextAsync()).Type);
        Assert.Equal("heartbeat", (await stream.NextAsync()).Type);
        Assert.Equal("heartbeat", (await stream.NextAsync()).Type);
    }

    [Fact]
    public async Task Disconnecting_releases_the_subscription()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["LaunchIntents:EventsHeartbeatSeconds"] = "1"
        });
        var broadcaster = factory.Services.GetRequiredService<LaunchIntentEventBroadcaster>();
        using var bot = CreateBot(factory);
        var stream = await SseReader.OpenAsync(bot);
        Assert.Equal("connected", (await stream.NextAsync()).Type);
        Assert.Equal(1, broadcaster.SubscriberCount);

        await stream.DisposeAsync();

        await WaitUntilAsync(() => broadcaster.SubscriberCount == 0);
    }

    [Fact]
    public async Task Subscriber_limit_rejects_extra_connections()
    {
        using var factory = new SonicRelayApiFactory(new Dictionary<string, string?>
        {
            ["LaunchIntents:EventsMaxSubscribers"] = "1"
        });
        using var bot = CreateBot(factory);
        await using var first = await SseReader.OpenAsync(bot);
        Assert.Equal("connected", (await first.NextAsync()).Type);

        var second = await bot.GetAsync("/api/launch-intents/events");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
    }

    [Fact]
    public async Task Reconnect_reconciles_ready_intents_committed_while_disconnected()
    {
        using var factory = new SonicRelayApiFactory();
        using var bot = CreateBot(factory);
        var (intentId, sessionId, publisher) = await CreateConsumedShareAsync(factory, bot);
        (await publisher.PostAsJsonAsync($"/api/launch-intents/share/{intentId}/complete", new { sessionId }))
            .EnsureSuccessStatusCode(); // no subscriber connected: this notification is not observed

        await using var stream = await SseReader.OpenAsync(bot);
        Assert.Equal("connected", (await stream.NextAsync()).Type);
        var pending = await bot.GetFromJsonAsync<JsonElement>("/api/launch-intents/pending");

        var recovered = Assert.Single(pending.EnumerateArray());
        Assert.Equal(intentId, recovered.GetProperty("id").GetGuid());
        Assert.Equal("session_ready", recovered.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Broadcaster_delivers_to_every_subscriber_in_order()
    {
        var broadcaster = new LaunchIntentEventBroadcaster();
        await using var first = broadcaster.Subscribe();
        await using var second = broadcaster.Subscribe();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        broadcaster.Publish(LaunchIntentEventTypes.SessionReady, a);
        broadcaster.Publish(LaunchIntentEventTypes.SessionReady, b);

        foreach (var subscription in new[] { first, second })
        {
            Assert.True(subscription.Reader.TryRead(out var one));
            Assert.True(subscription.Reader.TryRead(out var two));
            Assert.Equal(a, one!.IntentId);
            Assert.Equal(b, two!.IntentId);
            Assert.True(two.Sequence > one.Sequence);
        }
    }

    [Fact]
    public async Task Broadcaster_disconnects_a_slow_subscriber_without_affecting_others()
    {
        var broadcaster = new LaunchIntentEventBroadcaster(bufferCapacity: 2);
        await using var slow = broadcaster.Subscribe();
        await using var fast = broadcaster.Subscribe();

        for (var i = 0; i < 3; i++)
        {
            broadcaster.Publish(LaunchIntentEventTypes.SessionReady, Guid.NewGuid());
            while (fast.Reader.TryRead(out _)) { }
        }

        Assert.True(slow.Overflowed);
        Assert.False(fast.Overflowed);
        Assert.Equal(1, broadcaster.SubscriberCount);
        // The slow reader drains what it had, then sees completion and must reconnect + reconcile.
        Assert.True(slow.Reader.TryRead(out _));
        Assert.True(slow.Reader.TryRead(out _));
        Assert.False(await slow.Reader.WaitToReadAsync());
    }

    [Fact]
    public async Task Broadcaster_dispose_unsubscribes()
    {
        var broadcaster = new LaunchIntentEventBroadcaster();
        var subscription = broadcaster.Subscribe();
        Assert.Equal(1, broadcaster.SubscriberCount);

        await subscription.DisposeAsync();
        broadcaster.Publish(LaunchIntentEventTypes.SessionReady, Guid.NewGuid());

        Assert.Equal(0, broadcaster.SubscriberCount);
        Assert.False(await subscription.Reader.WaitToReadAsync());
    }

    private static HttpClient CreateBot(SonicRelayApiFactory factory)
    {
        var bot = factory.CreateClient();
        bot.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ServiceToken);
        return bot;
    }

    private static async Task<(Guid IntentId, Guid SessionId, HttpClient Publisher)> CreateConsumedShareAsync(
        SonicRelayApiFactory factory, HttpClient bot)
    {
        var created = await bot.PostAsJsonAsync("/api/launch-intents/share", new
        {
            provider = "discord", guildId = "guild-1", channelId = "channel-2", requestedByUserId = "user-3"
        });
        created.EnsureSuccessStatusCode();
        var launch = await created.Content.ReadFromJsonAsync<JsonElement>();
        var url = launch.GetProperty("launchUrl").GetString()!;
        var token = url[(url.LastIndexOf('/') + 1)..];

        var publisher = factory.CreateClient();
        await DeviceIdentityTestHelper.BootstrapAndAuthorizeAsync(
            publisher, DeviceTypes.WindowsPublisher, DevicePlatforms.Windows);
        (await publisher.PostAsJsonAsync("/api/launch-intents/share/consume", new { token })).EnsureSuccessStatusCode();
        var session = await (await publisher.PostAsJsonAsync("/api/sessions", new { maxViewers = 3, mode = "screen_share" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return (launch.GetProperty("id").GetGuid(), session.GetProperty("id").GetGuid(), publisher);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not met.");
            await Task.Delay(25);
        }
    }

    private sealed record SseFrame(string? Type, string Data, string? Id, string? Retry);

    /// <summary>Minimal text/event-stream reader for asserting on the wire format.</summary>
    private sealed class SseReader : IAsyncDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly StreamReader _reader;
        private readonly CancellationTokenSource _cancellation = new();

        private SseReader(HttpResponseMessage response, Stream body)
        {
            _response = response;
            _reader = new StreamReader(body);
        }

        public string? ContentType => _response.Content.Headers.ContentType?.MediaType;

        public static async Task<SseReader> OpenAsync(HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/launch-intents/events");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return new SseReader(response, await response.Content.ReadAsStreamAsync());
        }

        public async Task<SseFrame> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
            timeout.CancelAfter(Timeout);
            string? type = null, id = null, retry = null;
            var data = new List<string>();
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout.Token)
                    ?? throw new EndOfStreamException("SSE stream ended.");
                if (line.Length == 0)
                {
                    if (type is null && data.Count == 0) continue;
                    return new SseFrame(type, string.Join('\n', data), id, retry);
                }
                if (line.StartsWith(':')) continue;
                var separator = line.IndexOf(':');
                var field = separator < 0 ? line : line[..separator];
                var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
                switch (field)
                {
                    case "event": type = value; break;
                    case "data": data.Add(value); break;
                    case "id": id = value; break;
                    case "retry": retry = value; break;
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _reader.Dispose();
            _response.Dispose();
            _cancellation.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
