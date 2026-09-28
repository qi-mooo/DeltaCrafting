using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L2;

/// <summary>Only blocked checks are cached; every allowed task requires a fresh Steam response.</summary>
public sealed class SteamActivityGuard(ISteamActivitySource source, IClock clock)
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);
    private sealed record CachedBlock(string ApiKey, string SteamId, SteamActivityBlock Block);
    private CachedBlock? _cached;

    public SteamActivityBlock? GetBlock(SteamActivitySettings settings)
    {
        var cached = Volatile.Read(ref _cached);
        return settings.Enabled && cached is not null && cached.ApiKey == settings.ApiKey
            && cached.SteamId == settings.SteamId && clock.Now < cached.Block.RetryAt
            ? cached.Block : null;
    }

    // The coordinator holds its execution lock for the whole check and task.
    public async Task<SteamActivityBlock?> CheckAsync(SteamActivitySettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!settings.Enabled)
        {
            Volatile.Write(ref _cached, null);
            return null;
        }
        if (GetBlock(settings) is { } waiting) return waiting;
        string key = settings.ApiKey, id = settings.SteamId;
        var result = await source.CheckAsync(key, id, ct);
        ct.ThrowIfCancellationRequested();
        if (result.State == SteamActivityState.NotPlaying)
        {
            Volatile.Write(ref _cached, null);
            return null;
        }
        var block = new SteamActivityBlock(result.Detail, clock.Now + RetryDelay);
        Volatile.Write(ref _cached, new CachedBlock(key, id, block));
        return block;
    }
}
