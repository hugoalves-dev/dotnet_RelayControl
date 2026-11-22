namespace SonicRelay.Api.Services;

public sealed class LaunchIntentOptions
{
    public const string SectionName = "LaunchIntents";
    public string? ServiceToken { get; set; }
    public string PublicBaseUrl { get; set; } = "https://framerelay.hugojava.dev";
    public int ShareTtlSeconds { get; set; } = 600;
    public int WatchTtlSeconds { get; set; } = 1800;

    /// <summary>Keep-alive cadence on the bot SSE stream; below Cloudflare's ~100s idle cutoff.</summary>
    public int EventsHeartbeatSeconds { get; set; } = 20;

    /// <summary>Upper bound on concurrent bot SSE connections per API instance.</summary>
    public int EventsMaxSubscribers { get; set; } = 8;
}
