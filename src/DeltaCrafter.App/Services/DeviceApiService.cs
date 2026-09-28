using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L3;
using Microsoft.UI.Dispatching;

namespace DeltaCrafter.App.Services;

/// <summary>在 UI 线程读计划/写设置,游戏操作仍由原协调器的执行闸门管理。</summary>
public sealed class DeviceApiService : IDisposable
{
    private readonly AppHost _host;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DeviceApiCoordinator _api;
    private CancellationTokenSource? _remoteStop;
    private Task? _remoteRun;

    public string StatusText => _api.StatusText;
    public event Action? Changed;

    public DeviceApiService(AppHost host)
    {
        _host = host;
        _api = new DeviceApiCoordinator(
            ct => OnUiAsync(Snapshot, ct),
            (action, ct) => OnUiAsync(() => Execute(action), ct), host.Log, UpdateSettingsAsync);
        _api.Changed += () => _dispatcher.TryEnqueue(() => Changed?.Invoke());
    }

    public void Apply() => _api.Apply(_host.Settings.DeviceApi);

    private DeviceStatus Snapshot() => DeviceApiCoordinator.CreateStatus(
        typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        DateTimeOffset.Now, _host.Coordinator.Status, _host.Coordinator.IsRunning,
        _host.Settings, _host.Plan, _host.Coordinator.ScheduleSnapshot(),
        _host.SteamStatus.Snapshot(_host.Settings.SteamActivity));

    private async Task<DeviceActionResult> UpdateSettingsAsync(DeviceSettingsRequest update, CancellationToken ct)
    {
        DeviceActionResult result = new(409, "busy");
        await _host.Coordinator.RunBetweenRoundsAsync(async () =>
        {
            result = await OnUiAsync(() =>
            {
                if (!_host.Settings.DeviceApi.Enabled || !_host.Settings.DeviceApi.AllowControl)
                    return new DeviceActionResult(403, "control_disabled");
                if (_host.Coordinator.RunsBlocked) return new DeviceActionResult(409, "update_in_progress");
                if (!update.IsValid()) return new DeviceActionResult(400, "invalid_settings");
                if (update.Facility is { } facility)
                {
                    var key = FacilityKeys.All.Single(k => FacilityKeys.JsonKey(k) == facility);
                    _host.PlanVm.UpdateDeviceFacility(key, update.Enabled,
                        update.CraftMode is { } mode ? Enum.Parse<CraftMode>(mode) : null);
                }
                if (update.AutoLoopEnabled is { } loop) _host.PlanVm.AutoLoopEnabled = loop;
                if (update.SteamDetectionEnabled is { } steam) _host.SettingsVm.SteamActivityEnabled = steam;
                if (update.AfterRun is { } after) _host.SettingsVm.AfterRunIndex = (int)Enum.Parse<AfterRunAction>(after);
                _host.Log.Information("设备 API 设置已保存。");
                return new DeviceActionResult(200, "saved");
            }, ct);
        }, ct);
        return result;
    }

    private DeviceActionResult Execute(string action)
    {
        if (!_host.Settings.DeviceApi.Enabled || !_host.Settings.DeviceApi.AllowControl)
            return new(403, "control_disabled");
        switch (action)
        {
            case "start":
                if (_host.Coordinator.RunsBlocked)
                    return new(409, "update_in_progress");
                if (_host.Coordinator.IsRunning || _remoteRun is { IsCompleted: false })
                    return new(409, "already_running");
                _remoteStop?.Dispose();
                _remoteStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var token = _remoteStop.Token;
                _remoteRun = Task.Run(async () =>
                {
                    try { await _host.Coordinator.RunOnceAsync("设备 API", token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception ex) { _host.Log.Error(ex, "设备 API 执行请求失败。"); }
                });
                break;
            case "stop":
                // 同时关闭自动调度,避免十秒后马上重新执行。
                _host.PlanVm.AutoLoopEnabled = false;
                _remoteStop?.Cancel();
                _host.Coordinator.RequestStop();
                break;
            case "pause":
            case "resume":
                _host.PlanVm.AutoLoopEnabled = action == "resume";
                break;
            default:
                return new(400, "invalid_action");
        }
        _host.Log.Information("设备 API 控制:{Action}", action);
        return new(action is "start" or "stop" ? 202 : 200, "accepted");
    }

    private Task<T> OnUiAsync<T>(Func<T> action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                _shutdown.Token.ThrowIfCancellationRequested();
                completion.TrySetResult(action());
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetException(new InvalidOperationException("应用正在退出。"));
        return completion.Task.WaitAsync(ct);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _api.Dispose();
    }
}
