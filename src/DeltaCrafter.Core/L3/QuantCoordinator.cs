using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;

namespace DeltaCrafter.Core.L3;

/// <summary>Windows 与后续设备端共享的量化工作区；收费查询只由显式刷新触发。</summary>
public sealed class QuantCoordinator
{
    private readonly IAmmoMarketSource _source;
    private readonly JsonStoreBrick _store;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private QuantWorkspace _workspace;
    private readonly Dictionary<long, AmmoPriceHistory> _history = [];

    public QuantCoordinator(IAmmoMarketSource source, JsonStoreBrick store, string path)
    {
        _source = source;
        _store = store;
        _path = path;
        _workspace = store.LoadOrCreate(path, () => new QuantWorkspace());
        _workspace.Validate();
    }

    public QuantSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate) return new(1, _workspace.Forecasts.ToArray(), _workspace.Watchlist.ToArray(),
            _workspace.Trades.ToArray(), QuantAccounting.Summarize(_workspace.Trades, now));
    }

    public (decimal Budget, decimal FeePercent) Preferences
    { get { lock (_gate) return (_workspace.Budget, _workspace.FeePercent); } }

    public async Task<AmmoForecast> RefreshAsync(int grade, string token, CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct)) throw new InvalidOperationException("子弹数据正在刷新，请稍候。");
        try
        {
            var result = await _source.FetchLowPricesAsync(grade, token, ct);
            ct.ThrowIfCancellationRequested();
            Mutate(next =>
            {
                next.Forecasts.RemoveAll(f => f.Grade == grade);
                next.Forecasts.Add(result);
            });
            return result;
        }
        finally { _refresh.Release(); }
    }

    public void SavePreferences(decimal budget, decimal feePercent) => Mutate(next =>
    { next.Budget = budget; next.FeePercent = feePercent; });

    public AmmoPriceHistory? CachedHistory(long objectId)
    { lock (_gate) return _history.GetValueOrDefault(objectId); }

    public async Task<AmmoPriceHistory> RefreshHistoryAsync(long objectId, string token, CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct)) throw new InvalidOperationException("数据正在刷新，请稍候。");
        try
        {
            var result = await _source.FetchHistoryAsync(objectId, token, ct);
            ct.ThrowIfCancellationRequested();
            lock (_gate) _history[objectId] = result;
            return result;
        }
        finally { _refresh.Release(); }
    }

    public void SaveWatch(AmmoWatchItem item) => Mutate(next =>
    {
        item.Validate();
        next.Watchlist.RemoveAll(w => w.Id == item.Id);
        next.Watchlist.Add(item);
    });

    public void RemoveWatch(long id) => Mutate(next => next.Watchlist.RemoveAll(w => w.Id == id));

    public void RecordPurchase(AmmoTrade trade) => Mutate(next =>
    {
        trade.Validate();
        if (trade.IsClosed || next.Trades.Any(t => t.Id == trade.Id)) throw new ArgumentException("重复或已完成的买入记录。");
        next.Trades.Add(trade);
    });

    public void RecordSale(Guid id, int quantity, decimal price, decimal feePercent, DateTimeOffset now) => Mutate(next =>
    {
        int index = next.Trades.FindIndex(t => t.Id == id);
        if (index < 0 || next.Trades[index].IsClosed) throw new InvalidOperationException("该持仓不存在或已全部卖出。");
        var trade = next.Trades[index];
        if (quantity <= 0 || quantity > trade.Quantity) throw new ArgumentException("卖出数量超过持仓。");
        var closed = trade with { Quantity = quantity, SellPrice = price, FeePercent = feePercent, ClosedAt = now };
        closed.Validate();
        next.Trades[index] = closed;
        if (quantity < trade.Quantity)
            next.Trades.Add(trade with { Id = Guid.NewGuid(), Quantity = trade.Quantity - quantity });
    });

    private void Mutate(Action<QuantWorkspace> change)
    {
        lock (_gate)
        {
            var next = new QuantWorkspace
            {
                Forecasts = [.. _workspace.Forecasts], Watchlist = [.. _workspace.Watchlist],
                Trades = [.. _workspace.Trades], Budget = _workspace.Budget, FeePercent = _workspace.FeePercent,
            };
            change(next);
            next.Validate();
            _store.Save(_path, next);
            _workspace = next;
        }
    }
}
