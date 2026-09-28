using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L2;

/// <summary>Read-only display polling, independent of the five-minute task deferral.</summary>
public sealed class SteamStatusMonitor(ISteamActivitySource source, IClock clock)
{
    private sealed record Observation(string ApiKey, string SteamId, DeviceGameStatus Status);
    private Observation? _observation;

    public DeviceGameStatus Snapshot(SteamActivitySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.SteamId))
            return new("Unavailable", "Steam 未配置", null);
        var observation = Volatile.Read(ref _observation);
        if (observation is null || observation.ApiKey != settings.ApiKey || observation.SteamId != settings.SteamId)
            return new("Unknown", "游戏状态查询中", null);
        if (observation.Status.CheckedAt is not { } time || clock.Now - time > TimeSpan.FromSeconds(90))
            return new("Unavailable", "游戏状态已过期", observation.Status.CheckedAt);
        return observation.Status;
    }

    public async Task RefreshAsync(SteamActivitySettings settings, CancellationToken ct)
    {
        string key = settings.ApiKey, id = settings.SteamId;
        var result = await source.CheckAsync(key, id, ct);
        Volatile.Write(ref _observation, new Observation(key, id,
            new DeviceGameStatus(result.State.ToString(), result.Detail, clock.Now)));
    }
}
