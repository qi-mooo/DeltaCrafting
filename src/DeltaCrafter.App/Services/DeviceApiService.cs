using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
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
    private readonly DeviceFirmwareStore _firmware;
    private readonly DeviceFirmwareStore _harpFirmware;
    public string FirmwareStatus { get; private set; } = "尚未准备 S3 固件包";
    public string HarpFirmwareStatus { get; private set; } = "尚未准备 Harp USB 固件包";

    public string StatusText => _api.StatusText;
    public event Action? Changed;

    public DeviceApiService(AppHost host)
    {
        _host = host;
        _firmware = new DeviceFirmwareStore(Path.Combine(host.Paths.Root, "firmware"));
        _harpFirmware = new DeviceFirmwareStore(Path.Combine(host.Paths.Root, "harp-firmware"), DeviceFirmwareTarget.Harp);
        if (_harpFirmware.Current is { } harp) HarpFirmwareStatus = "可供更新: " + harp.FirmwareVersion;
        if (_firmware.Current is { } current) FirmwareStatus = "可供更新: " + current.FirmwareVersion;
        _api = new DeviceApiCoordinator(
            ct => OnUiAsync(Snapshot, ct),
            (action, ct) => OnUiAsync(() => Execute(action), ct), host.Log, UpdateSettingsAsync,
            (key, ct) => OnUiAsync(() => DeviceItemList.CreateWithMetadata(_host.Plan.For(key), _host.ItemsFor(key)), ct),
            async (query, ct) =>
            {
                string token = await OnUiAsync(() => _host.Settings.ManufactureApi.Token, ct);
                return await _host.DataTools.FetchAsync(query, token, ct);
            }, (code, ct) => OnUiAsync(() => _host.DataTools.CopyGunCode(code), ct), _host.DataTools.ImageAsync, _firmware, _harpFirmware);
        _api.Changed += () => _dispatcher.TryEnqueue(() => Changed?.Invoke());
        string bundle = Path.Combine(AppContext.BaseDirectory, "Firmware", "DeltaCrafter-esp32s3.zip");
        if (File.Exists(bundle)) _ = ImportFirmwareAsync(bundle, onlyNewer: true);
        string harpBundle = Path.Combine(AppContext.BaseDirectory, "Firmware", "DeltaHarp-esp32s3-usb.zip");
        if (File.Exists(harpBundle)) _ = ImportHarpFirmwareAsync(harpBundle, onlyNewer: true);
    }

    public async Task ImportFirmwareAsync(string path, bool onlyNewer = false)
    {
        try
        {
            var manifest = await Task.Run(() => _firmware.Import(path, onlyNewer));
            FirmwareStatus = "可供更新: " + manifest.FirmwareVersion;
            _host.Log.Information("S3 在线固件已准备: {Version}", manifest.FirmwareVersion);
        }
        catch (Exception ex)
        {
            FirmwareStatus = "固件导入失败: " + ex.Message;
            _host.Log.Warning(ex, "S3 固件导入失败。");
        }
        _dispatcher.TryEnqueue(() => Changed?.Invoke());
    }

    public void Apply() => _api.Apply(_host.Settings.DeviceApi);

    public async Task ImportHarpFirmwareAsync(string path, bool onlyNewer = false)
    {
        try
        {
            var manifest = await Task.Run(() => _harpFirmware.Import(path, onlyNewer));
            HarpFirmwareStatus = "可供更新: " + manifest.FirmwareVersion;
            _host.Log.Information("Harp USB 在线固件已准备: {Version}", manifest.FirmwareVersion);
        }
        catch (Exception ex)
        {
            HarpFirmwareStatus = "固件导入失败: " + ex.Message;
            _host.Log.Warning(ex, "Harp USB 固件导入失败。");
        }
        _dispatcher.TryEnqueue(() => Changed?.Invoke());
    }

    private DeviceStatus Snapshot()
    {
        var status = DeviceApiCoordinator.CreateStatus(
            typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            DateTimeOffset.Now, _host.Coordinator.Status, _host.Coordinator.IsRunning,
            _host.Settings, _host.Plan, _host.Coordinator.ScheduleSnapshot(),
            _host.SteamStatus.Snapshot(_host.Settings.SteamActivity));
        return status with
        {
            ItemSelectionSupported = true, CloseGameSupported = true,
            DataRefreshSupported = true, DataRefresh = _host.ProfitPlan.DataRefresh,
            ProfitRefreshSupported = true, ToolsSupported = true,
            Facilities = status.Facilities.Select(f =>
            {
                var key = FacilityKeys.All.Single(k => FacilityKeys.JsonKey(k) == f.Key);
                var items = _host.ItemsFor(key);
                return f with
                {
                    ProfitRefresh = _host.ProfitPlan.ProfitStatus(key),
                    ItemLabel = CatalogItemLabel.ForName(items, f.ItemName),
                    PlannedItemLabel = CatalogItemLabel.ForName(items, f.PlannedItemName),
                };
            }).ToArray(),
        };
    }

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
                    if (update.PlannedItemName is { } item)
                    {
                        var error = DeviceItemList.ValidateSelection(_host.Plan.For(key), _host.CatalogNamesFor(key), item);
                        if (error is not null) return error;
                    }
                    _host.PlanVm.UpdateDeviceFacility(key, update.Enabled,
                        update.CraftMode is { } mode ? Enum.Parse<CraftMode>(mode) : null, update.PlannedItemName);
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

    private DeviceActionResult Execute(DeviceActionRequest request)
    {
        if (!_host.Settings.DeviceApi.Enabled || !_host.Settings.DeviceApi.AllowControl)
            return new(403, "control_disabled");
        string action = request.Action;
        switch (action)
        {
            case "refresh-profit":
                var key = FacilityKeys.All.Single(k => FacilityKeys.JsonKey(k) == request.Facility);
                if (_host.Plan.For(key).Mode != CraftMode.HourlyProfit) return new(409, "hourly_profit_required");
                if (!_host.ProfitPlan.TryRefreshProfit(key)) return new(409, "busy");
                break;
            case "refresh-data":
                if (_host.Coordinator.RunsBlocked) return new(409, "update_in_progress");
                if (!_host.ProfitPlan.TryRefreshData()) return new(409, "already_refreshing");
                break;
            case "start":
            case "sync":
            case "close-game":
                if (_host.Coordinator.RunsBlocked)
                    return new(409, "update_in_progress");
                if (_host.Coordinator.IsRunning || _remoteRun is { IsCompleted: false })
                    return new(409, "already_running");
                _remoteStop?.Dispose();
                _remoteStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                var token = _remoteStop.Token;
                _remoteRun = Task.Run(async () =>
                {
                    try
                    {
                        if (action == "sync")
                            await _host.Coordinator.SyncFacilitiesAsync("设备 API 识别当前任务", token);
                        else if (action == "close-game") await _host.Coordinator.CloseGameAsync(token);
                        else await _host.Coordinator.RunOnceAsync("设备 API 开始制造", token);
                    }
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
        return new(action is "start" or "sync" or "close-game" or "refresh-data" or "refresh-profit" or "stop" ? 202 : 200, "accepted");
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
