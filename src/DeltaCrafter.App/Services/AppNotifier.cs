using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.App.Services;

/// <summary>系统 Toast + 可选 Bark 推送。Bark 在后台发送,失败只记日志,不阻塞自动化线程。</summary>
public sealed class AppNotifier(ToastNotifier toast, Func<BarkSettings> bark, IClock clock, ILogger log) : INotifier
{
    private readonly ILogger _log = log.ForContext<AppNotifier>();
    private readonly BarkClient _client = new();
    private readonly NotificationThrottle _throttle = new(clock);

    public void Notify(NotificationKind kind, string title, string message)
    {
        toast.Notify(kind, title, message);
        var settings = bark();
        if (!settings.Allows(kind)) return;
        if (!_throttle.TryAcquire(kind, title, settings))
        {
            _log.Debug("Bark 推送「{Title}」已达重复上限,本次跳过。", title);
            return;
        }
        string url = settings.Url, sound = settings.SoundFor(kind);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _client.SendAsync(url, kind, title, message, sound, CancellationToken.None);
                if (!result.Success) _log.Warning("Bark 推送「{Title}」失败:{Detail}", title, result.Detail);
            }
            catch (Exception ex)
            {
                _log.Warning("Bark 推送「{Title}」异常:{Type}", title, ex.GetType().Name);
            }
        });
    }

    public Task<BarkResult> SendTestAsync(string url, string sound) =>
        _client.SendAsync(url, NotificationKind.RoundCompleted, "DeltaCrafter 测试推送", "Bark 推送已连通。", sound, CancellationToken.None);

    public void Unregister() => toast.Unregister();
}
