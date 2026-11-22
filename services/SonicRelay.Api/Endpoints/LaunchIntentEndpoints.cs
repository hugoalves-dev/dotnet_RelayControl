using System.Net;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SonicRelay.Api.Authorization;
using SonicRelay.Api.Contracts;
using SonicRelay.Api.Services;
using SonicRelay.Domain.DeviceIdentities;
using SonicRelay.Infrastructure.Persistence;

namespace SonicRelay.Api.Endpoints;

public static class LaunchIntentEndpoints
{
    public static IEndpointRouteBuilder MapLaunchIntentEndpoints(this IEndpointRouteBuilder app)
    {
        var bot = app.MapGroup("/api/launch-intents").WithTags("Launch intents")
            .RequireAuthorization("launch-intents:bot");
        bot.MapPost("/share", CreateShareAsync);
        bot.MapGet("/pending", ListPendingAsync);
        bot.MapGet("/events", StreamEvents);
        bot.MapGet("/{intentId:guid}", GetStatusAsync);
        bot.MapPost("/{intentId:guid}/published", MarkPublishedAsync);
        bot.MapPost("/watch", CreateWatchAsync);

        var device = app.MapGroup("/api/launch-intents").WithTags("Launch intents")
            .RequireAuthorization("DeviceAuthenticated");
        device.MapPost("/share/consume", ConsumeShareAsync).RequireAuthorization("session:create");
        device.MapPost("/share/{intentId:guid}/complete", CompleteShareAsync).RequireAuthorization("session:create");
        device.MapPost("/watch/resolve", ResolveWatchAsync).RequireAuthorization("session:join");

        app.MapGet("/open/{mode}/{token}", LandingPageAsync).AllowAnonymous().WithTags("Launch links");
        return app;
    }

    private static async Task<IResult> CreateShareAsync(CreateShareLaunchIntentRequest request,
        LaunchIntentService service, CancellationToken ct)
    {
        try
        {
            var (intent, launchUrl) = await service.CreateShareAsync(request.Provider, request.GuildId,
                request.ChannelId, request.RequestedByUserId, request.TtlSeconds, ct);
            return Results.Created($"/api/launch-intents/{intent.Id}",
                new CreateShareLaunchIntentResponse(intent.Id, launchUrl, intent.ExpiresAt));
        }
        catch (LaunchIntentException error)
        {
            return Results.BadRequest(new { code = error.Code });
        }
    }

    private static async Task<IResult> ListPendingAsync(LaunchIntentService service, CancellationToken ct)
    {
        var intents = await service.ListRecoverableAsync(ct);
        return Results.Ok(intents.Select(x => new PendingShareLaunchIntentResponse(
            x.Id, x.GuildId, x.ChannelId, x.RequestedByUserId,
            x.ExpiresAt <= DateTimeOffset.UtcNow ? "expired" : x.Status, x.ExpiresAt)));
    }

    /// <summary>
    /// Server-sent events for the bot. The subscription is registered before the response starts
    /// and the first frame is <c>connected</c>; a client that reconciles <c>GET /pending</c> after
    /// receiving it cannot miss a <c>session_ready</c> committed in between (duplicates are possible).
    /// </summary>
    private static IResult StreamEvents(HttpContext context, LaunchIntentEventBroadcaster broadcaster,
        IOptions<LaunchIntentOptions> options, TimeProvider timeProvider)
    {
        if (broadcaster.SubscriberCount >= Math.Max(1, options.Value.EventsMaxSubscribers))
            return Results.Json(new { code = "too_many_subscribers" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var subscription = broadcaster.Subscribe();
        context.Response.RegisterForDisposeAsync(subscription);
        context.Response.Headers.CacheControl = "no-cache, no-store";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var heartbeat = TimeSpan.FromSeconds(Math.Clamp(options.Value.EventsHeartbeatSeconds, 1, 60));
        return TypedResults.ServerSentEvents(
            ReadEventsAsync(subscription, heartbeat, timeProvider, context.RequestAborted));
    }

    internal static readonly TimeSpan EventsReconnectInterval = TimeSpan.FromSeconds(5);

    private static async IAsyncEnumerable<SseItem<string>> ReadEventsAsync(ILaunchIntentSubscription subscription,
        TimeSpan heartbeat, TimeProvider timeProvider, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var _ = subscription;
        yield return new SseItem<string>("{}", "connected") { ReconnectionInterval = EventsReconnectInterval };

        var reader = subscription.Reader;
        Task<bool>? pendingRead = null;
        while (!ct.IsCancellationRequested)
        {
            pendingRead ??= reader.WaitToReadAsync(ct).AsTask();
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(heartbeat, timeProvider, delayCancellation.Token);
            var completed = await Task.WhenAny(pendingRead, delay);
            await delayCancellation.CancelAsync();
            if (completed != pendingRead)
            {
                yield return new SseItem<string>("{}", "heartbeat");
                continue;
            }

            var hasMore = await pendingRead;
            pendingRead = null;
            if (!hasMore) yield break; // overflowed: the client reconnects and reconciles
            while (reader.TryRead(out var item))
            {
                yield return new SseItem<string>(
                    $"{{\"intentId\":\"{item.IntentId:D}\"}}", item.Type) { EventId = item.Sequence.ToString() };
            }
        }
    }

    private static async Task<IResult> GetStatusAsync(Guid intentId, int? watchTtlSeconds,
        LaunchIntentService service, CancellationToken ct)
    {
        try
        {
            var (intent, watchUrl, watchExpiresAt) = await service.GetStatusAsync(intentId, watchTtlSeconds, ct);
            return Results.Ok(new ShareLaunchIntentStatusResponse(
                intent.Id, intent.Status, intent.SessionId, watchUrl, watchExpiresAt ?? intent.ExpiresAt));
        }
        catch (LaunchIntentException error)
        {
            return Results.NotFound(new { code = error.Code });
        }
    }

    private static async Task<IResult> MarkPublishedAsync(Guid intentId, MarkSharePublishedRequest request,
        LaunchIntentService service, CancellationToken ct)
    {
        if (request.MessageId?.Length > 64) return Results.BadRequest(new { code = "invalid_message_id" });
        try
        {
            await service.MarkPublishedAsync(intentId, request.MessageId, ct);
            return Results.NoContent();
        }
        catch (LaunchIntentException error)
        {
            return Results.NotFound(new { code = error.Code });
        }
    }

    private static async Task<IResult> CreateWatchAsync(CreateWatchLaunchRequest request,
        IOptions<LaunchIntentOptions> options, LaunchIntentService service, CancellationToken ct)
    {
        try
        {
            var (token, expiresAt) = await service.CreateWatchCapabilityAsync(request.Code, request.TtlSeconds, ct);
            var response = new CreateWatchLaunchResponse(
                $"{options.Value.PublicBaseUrl.TrimEnd('/')}/open/watch/{token}", expiresAt);
            return Results.Ok(response);
        }
        catch (LaunchIntentException error)
        {
            return Results.BadRequest(new { code = error.Code });
        }
    }

    private static async Task<IResult> ConsumeShareAsync(ConsumeShareLaunchIntentRequest request,
        ClaimsPrincipal principal, AppDbContext db, LaunchIntentService service, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null || device.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();
        try
        {
            var intent = await service.ConsumeShareAsync(request.Token, device.Id, ct);
            return Results.Ok(new ConsumeShareLaunchIntentResponse(intent.Id, intent.ExpiresAt));
        }
        catch (LaunchIntentException error)
        {
            return Results.Conflict(new { code = error.Code });
        }
    }

    private static async Task<IResult> CompleteShareAsync(Guid intentId, CompleteShareLaunchIntentRequest request,
        ClaimsPrincipal principal, AppDbContext db, LaunchIntentService service, CancellationToken ct)
    {
        var device = await DeviceIdentityEndpoints.RequireDeviceAsync(principal, db, ct);
        if (device is null || device.Status != DeviceIdentityStatuses.Active) return Results.Unauthorized();
        try
        {
            await service.CompleteShareAsync(intentId, device.Id, request.SessionId, ct);
            return Results.NoContent();
        }
        catch (LaunchIntentException error)
        {
            return Results.Conflict(new { code = error.Code });
        }
    }

    private static async Task<IResult> ResolveWatchAsync(ResolveWatchLaunchRequest request,
        LaunchIntentService service, CancellationToken ct)
    {
        try
        {
            var sessionId = await service.ResolveWatchCapabilityAsync(request.Token, ct);
            return Results.Ok(new ResolveWatchLaunchResponse(sessionId));
        }
        catch (LaunchIntentException error)
        {
            return Results.NotFound(new { code = error.Code });
        }
    }

    private static IResult LandingPageAsync(string mode, string token, HttpContext context)
    {
        if (mode is not ("share" or "watch") || token.Length is < 32 or > 128)
            return Results.NotFound();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        var customUri = $"framerelay://open/{mode}/{Uri.EscapeDataString(token)}";
        var safeUri = WebUtility.HtmlEncode(customUri);
        var html = "<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\">" +
            "<title>Open FrameRelay</title><body><h1>Opening FrameRelay…</h1>" +
            $"<p><a rel=\"noreferrer\" href=\"{safeUri}\">Open FrameRelay</a></p>" +
            "<p id=\"fallback\" hidden>FrameRelay did not open. <a rel=\"noreferrer\" href=\"https://github.com/hugoalves-dev/dotnet_FrameRelay/releases/latest\">Install FrameRelay</a>, then select Open FrameRelay above.</p>" +
            $"<script>location.href='{safeUri}';setTimeout(()=>document.getElementById('fallback').hidden=false,1500)</script></body></html>";
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
