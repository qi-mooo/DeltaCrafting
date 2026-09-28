namespace DeltaCrafter.Core.L0;

public sealed record GameProcessIdentity(int ProcessId, DateTime StartedUtc, string ExecutablePath);

public sealed class SteamActivitySettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
    public string SteamId { get; set; } = "";
}

public enum SteamActivityState { NotPlaying, Playing, Unavailable }

public sealed record SteamActivityResult(SteamActivityState State, string Detail);

public sealed record SteamActivityBlock(string Detail, DateTimeOffset RetryAt);

public interface ISteamActivitySource
{
    Task<SteamActivityResult> CheckAsync(string apiKey, string steamId, CancellationToken ct);
}
