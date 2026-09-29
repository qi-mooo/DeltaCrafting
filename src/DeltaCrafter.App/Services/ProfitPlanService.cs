using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Microsoft.UI.Dispatching;
using Serilog;

namespace DeltaCrafter.App.Services;

/// <summary>目录只手动刷新;小时利润仅制造前或显式请求时查询。</summary>
public sealed class ProfitPlanService
{
    private readonly AppHost _host;
    private readonly ILogger _log;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly ManufactureApiClient _client = new();
    private readonly ItemMetadataApiClient _metadataClient = new();
    private readonly Dictionary<FacilityKey, DeviceDataRefreshStatus> _profits = [];
    private readonly SemaphoreSlim _queryGate = new(1, 1);
    private long _settingsVersion;
    private CancellationToken Stop => _host.AppStopToken;
    public string LastStatus { get; private set; } = "";
    public DeviceDataRefreshStatus DataRefresh { get; private set; }
    public event Action? Changed;

    private sealed record Request(FacilityKey Key, string Token, int Level, long Version);

    public ProfitPlanService(AppHost host, ILogger log)
    {
        _host = host;
        _log = log;
        DataRefresh = new(false, host.Catalog.UpdatedAt is { } updated
            ? $"目录更新于 {updated:MM-dd HH:mm}" : "尚未刷新物品列表", host.Catalog.UpdatedAt);
    }

    private Request Capture(FacilityKey key) => new(key, _host.Settings.ManufactureApi.Token,
        _host.Settings.ManufactureApi.LevelFor(key), _settingsVersion);

    public DeviceDataRefreshStatus ProfitStatus(FacilityKey key) =>
        _profits.GetValueOrDefault(key, new(false, "尚未查询利润"));

    public void SettingsChanged() => _settingsVersion++;

    // The coordinator already owns the round lock here. Never acquire it again.
    public async Task PrepareCraftAsync(FacilityPlan executionPlan, CancellationToken ct)
    {
        if (executionPlan.Mode != CraftMode.HourlyProfit) return;
        await RefreshProfitAsync(executionPlan.Key, executionPlan, ct);
    }

    public bool TryRefreshProfit(FacilityKey key)
    {
        if (_host.Plan.For(key).Mode != CraftMode.HourlyProfit || ProfitStatus(key).IsRunning
            || _host.Coordinator.RunsBlocked || _host.Coordinator.IsRunning) return false;
        SetProfit(key, new(true, "正在刷新利润物品"));
        _ = RefreshProfitFromButtonAsync(key);
        return true;
    }

    private async Task RefreshProfitFromButtonAsync(FacilityKey key)
    {
        try
        {
            await _host.Coordinator.RunBetweenRoundsAsync(() => RefreshProfitAsync(key, null, Stop), Stop);
        }
        catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
        catch (Exception ex) { _log.Warning("刷新利润物品失败:{Reason}", ex.Message); }
    }

    private async Task RefreshProfitAsync(FacilityKey key, FacilityPlan? executionPlan, CancellationToken ct)
    {
        try
        {
            var request = await OnUi(() =>
            {
                if (_host.Coordinator.RunsBlocked || _host.Plan.For(key).Mode != CraftMode.HourlyProfit)
                    throw new InvalidOperationException("制造模式或执行状态已改变,请重试。");
                SetProfit(key, new(true, "正在刷新利润物品"));
                return Capture(key);
            }, ct);
            var snapshot = await FetchAsync(request, ct);
            var best = snapshot.Best(CraftMode.HourlyProfit);
            await OnUi(() =>
            {
                if (Capture(key) != request || _host.Plan.For(key).Mode != CraftMode.HourlyProfit)
                    throw new InvalidOperationException("接口设置或制造模式已改变,请重试。");
                string name = CatalogNameResolver.Resolve(_host.ItemsFor(key), best.ItemName)
                    ?? throw new InvalidOperationException("推荐物品未收录,请先刷新物品列表。");
                var plan = _host.Plan.For(key);
                plan.ItemName = plan.MatchName = name;
                _host.SavePlan();
                if (executionPlan is not null) executionPlan.ItemName = executionPlan.MatchName = name;
                _host.PlanVm.RefreshFacilities([key]);
                LastStatus = $"{FacilityKeys.DisplayName(key)}: {name},每小时利润 {best.Profit:N0}";
                SetProfit(key, new(false, name, snapshot.FetchedAt));
                _host.PlanVm.NotifyProfitStatusChanged();
                _log.Information("数据帝推荐:{Result}", LastStatus);
                return true;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await OnUi(() => { SetProfit(key, new(false, "刷新失败: " + ex.Message)); return true; }, Stop);
            throw;
        }
        finally
        {
            if (!Stop.IsCancellationRequested)
                await OnUi(() =>
                {
                    if (ProfitStatus(key).IsRunning) SetProfit(key, new(false, "查询已取消"));
                    return true;
                }, Stop);
        }
    }

    public bool TryRefreshData()
    {
        if (DataRefresh.IsRunning || _host.Coordinator.RunsBlocked) return false;
        SetDataRefresh(new(true, "正在刷新物品列表", DataRefresh.CompletedAt));
        _ = RefreshDataAsync();
        return true;
    }

    private async Task RefreshDataAsync()
    {
        try
        {
            var requests = await OnUi(() => FacilityKeys.All.Select(Capture).ToArray(), Stop);
            var snapshots = new List<ManufactureMarketSnapshot>();
            foreach (var request in requests) snapshots.Add(await FetchAsync(request, Stop, catalogOnly: true));
            var metadata = await _metadataClient.FetchAsync(requests[0].Token, Stop);
            var builtIn = _host.Store.Load<ItemCatalog>(Path.Combine(AppContext.BaseDirectory, "Data", "items.json"));
            var catalog = ManufactureCatalog.Build(snapshots, builtIn);
            ItemMetadataCatalog.Apply(catalog, metadata);
            await _host.Coordinator.RunBetweenRoundsAsync(async () =>
            {
                await OnUi(() =>
                {
                    if (_host.Coordinator.RunsBlocked) throw new InvalidOperationException("客户端正在更新,请稍后刷新。");
                    if (requests.Any(r => r != Capture(r.Key)))
                        throw new InvalidOperationException("接口设置已改变,请重新刷新。");
                    _host.Store.Save(_host.Paths.ItemMetadataPath, metadata);
                    _host.ReplaceCatalog(catalog);
                    string detail = $"已刷新 {catalog.Facilities.Values.Sum(v => v.Count)} 个可制造物品";
                    SetDataRefresh(new(false, detail, DateTimeOffset.Now));
                    _log.Information("数据帝:{Result}", detail);
                    return true;
                }, Stop);
            }, Stop);
        }
        catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Warning("物品列表刷新失败:{Reason}", ex.Message);
            await OnUi(() =>
            {
                SetDataRefresh(new(false, "刷新失败: " + ex.Message, DataRefresh.CompletedAt));
                return true;
            }, Stop);
        }
    }

    private async Task<ManufactureMarketSnapshot> FetchAsync(Request request, CancellationToken ct, bool catalogOnly = false)
    {
        await _queryGate.WaitAsync(ct);
        try { return await _client.FetchAsync(request.Key, request.Level, request.Token, ct, catalogOnly); }
        finally { _queryGate.Release(); }
    }

    private void SetDataRefresh(DeviceDataRefreshStatus status) { DataRefresh = status; Changed?.Invoke(); }
    private void SetProfit(FacilityKey key, DeviceDataRefreshStatus status) { _profits[key] = status; Changed?.Invoke(); }

    private Task<T> OnUi<T>(Func<T> action, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(() =>
        {
            try { ct.ThrowIfCancellationRequested(); Stop.ThrowIfCancellationRequested(); completion.TrySetResult(action()); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })) completion.TrySetCanceled();
        return completion.Task.WaitAsync(ct);
    }
}
