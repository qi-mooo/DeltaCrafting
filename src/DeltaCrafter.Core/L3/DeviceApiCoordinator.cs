using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L3;

public sealed class DeviceApiCoordinator : IDisposable
{
    private readonly Func<CancellationToken, Task<DeviceStatus>> _status;
    private readonly Func<string, CancellationToken, Task<DeviceActionResult>> _action;
    private readonly Func<DeviceSettingsRequest, CancellationToken, Task<DeviceActionResult>>? _settings;
    private readonly ILogger _log;
    private DeviceApiServer? _server;
    public string StatusText { get; private set; } = "设备 API 已关闭";
    public event Action? Changed;

    public DeviceApiCoordinator(Func<CancellationToken, Task<DeviceStatus>> status,
        Func<string, CancellationToken, Task<DeviceActionResult>> action, ILogger log,
        Func<DeviceSettingsRequest, CancellationToken, Task<DeviceActionResult>>? updateSettings = null)
    {
        _status = status;
        _action = action;
        _settings = updateSettings;
        _log = log;
    }

    public void Apply(DeviceApiSettings settings)
    {
        Dispose();
        if (!settings.Enabled)
        {
            StatusText = "设备 API 已关闭";
            Changed?.Invoke();
            return;
        }
        try
        {
            _server = new DeviceApiServer(settings, _status, _action, _log, _settings);
            var server = _server;
            server.Failed += ex =>
            {
                if (!ReferenceEquals(_server, server)) return;
                StatusText = $"设备 API 已停止:{ex.Message}";
                Changed?.Invoke();
            };
            _server.Start();
            StatusText = $"正在监听 TCP {settings.Port} · {(settings.AllowControl ? "允许控制" : "只读监控")}";
            _log.Information("{Status}", StatusText);
        }
        catch (Exception ex)
        {
            Dispose();
            StatusText = $"设备 API 启动失败:{ex.Message}";
            _log.Error(ex, "设备 API 启动失败。");
        }
        Changed?.Invoke();
    }

    public static DeviceStatus CreateStatus(string appVersion, DateTimeOffset now,
        CoordinatorStatus status, bool isRunning, AppSettings settings, CraftPlanConfig plan,
        ScheduleState state, DeviceGameStatus? game = null) => new(
        1, appVersion, now, status.Mode.ToString(), status.Detail, isRunning,
        settings.AutoLoopEnabled, settings.DeviceApi.AllowControl,
        settings.AutoLoopEnabled ? status.NextRunAt : null,
        settings.AutoLoopEnabled ? Remaining(status.NextRunAt, now) : null,
        state.LastRunAt, state.LastRunSummary, state.LastRunFailed,
        FacilityKeys.All.Select(key =>
        {
            var runtime = state.Facilities.FirstOrDefault(f => f.Key == key) ?? new FacilityRuntime { Key = key };
            var planned = plan.For(key);
            return new DeviceFacilityStatus(FacilityKeys.JsonKey(key), FacilityKeys.DisplayName(key),
                planned.Enabled, planned.Mode.ToString(), planned.ItemName,
                runtime.Phase.ToString(), runtime.ItemName, runtime.ReadyAt,
                runtime.Phase == FacilityPhase.Crafting ? Remaining(runtime.ReadyAt, now) : null,
                runtime.ManualReason, runtime.ObservedAt,
                runtime.Phase == FacilityPhase.Crafting && runtime.StartedAt is { } start
                    && runtime.ReadyAt is { } end && end > start
                    ? (long)Math.Ceiling((end - start).TotalSeconds) : null);
        }).ToArray(), game, settings.SteamActivity.Enabled, settings.AfterRun.ToString());

    private static long? Remaining(DateTimeOffset? until, DateTimeOffset now) =>
        until is { } time ? Math.Max(0, (long)Math.Ceiling((time - now).TotalSeconds)) : null;

    public void Dispose()
    {
        var server = _server;
        _server = null;
        server?.Dispose();
    }
}
