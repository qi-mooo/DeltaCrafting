using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L3;

/// <summary>UI 步骤失败后只重启重跑一次；从整轮入口重新观察，不续接失败按钮。</summary>
internal static class RoundRecovery
{
    internal static async Task ExecuteAsync(Func<CancellationToken, Task> round,
        Func<StepFailedException, CancellationToken, Task> restart, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { await round(ct); }
        catch (StepFailedException ex) when (ex is not WarehouseFullException
            && ex.StepName is not ("前置检查" or "校验计划物品"))
        {
            ct.ThrowIfCancellationRequested();
            await restart(ex, ct);
            ct.ThrowIfCancellationRequested();
            // 故意在 catch 内调用：第二次失败直接交给外层记录、通知与退避。
            await round(ct);
        }
    }
}
