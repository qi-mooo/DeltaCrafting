using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L2;

/// <summary>Only the exact process created after our launch request is owned; PID reuse is not ownership.</summary>
public sealed class AssistantGameSession
{
    private GameProcessIdentity? _owned;

    public bool Record(GameProcessIdentity? process, DateTime? launchRequestedUtc)
    {
        if (process is null || launchRequestedUtc is not { } requested || process.StartedUtc < requested)
            return false;
        Volatile.Write(ref _owned, process);
        return true;
    }

    public bool IsRunning(Func<int, GameProcessIdentity?> read)
    {
        var owned = Volatile.Read(ref _owned);
        if (owned is null) return false;
        var current = read(owned.ProcessId);
        if (current is not null && current.ProcessId == owned.ProcessId
            && current.StartedUtc == owned.StartedUtc
            && string.Equals(current.ExecutablePath, owned.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            return true;
        Interlocked.CompareExchange(ref _owned, null, owned);
        return false;
    }
}
