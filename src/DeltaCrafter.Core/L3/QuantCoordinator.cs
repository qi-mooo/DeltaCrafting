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

    public QuantCoordinator(IAmmoMarketSource source, JsonStoreBrick store, string path)
    {
        _source = source;
        _store = store;
        _path = path;
        _workspace = store.LoadOrCreate(path, () => new QuantWorkspace());
        _workspace.Validate();
        // 0.5.0 did not persist the minute API identity on ledger rows. Recover only exact cached IDs.
        long ObjectId(long id) => _workspace.Forecasts.SelectMany(f => f.Entries).FirstOrDefault(e => e.Id == id)?.ObjectId ?? 0;
        _workspace.Watchlist = _workspace.Watchlist.Select(w => w.ObjectId == 0 ? w with { ObjectId = ObjectId(w.Id) } : w).ToList();
        _workspace.Trades = _workspace.Trades.Select(t => t.ObjectId == 0 ? t with { ObjectId = ObjectId(t.ItemId) } : t).ToList();
    }

    public QuantSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            var candidates = _workspace.Forecasts.Where(f => f.IsCurrent(now)).SelectMany(f => f.Entries)
                .GroupBy(e => e.Id).Select(g => g.OrderBy(e => e.Price).First())
                .Select(item => QuantStrategies.Evaluate(item, CachedHistory(item.ObjectId), _workspace.Options,
                    _workspace.Budget, _workspace.FeePercent, now))
                .OrderByDescending(c => c.Eligible).ThenByDescending(c => c.Score).ThenBy(c => c.Item.Id).ToArray();
            var positions = _workspace.Trades.Where(t => !t.IsClosed)
                .Select(t => QuantStrategies.Position(t, CachedHistory(t.ObjectId), _workspace.Options, now)).ToArray();
            return new(1, _workspace.Forecasts.ToArray(), _workspace.Watchlist.ToArray(),
                _workspace.Trades.ToArray(), QuantAccounting.Summarize(_workspace.Trades, now), _workspace.Options, candidates, positions);
        }
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
    { lock (_gate) return _workspace.Histories.FirstOrDefault(h => h.ObjectId == objectId); }

    public void SaveOptions(QuantOptions options) => Mutate(next => next.Options = options);

    public void SaveAnalysisSettings(QuantOptions options, decimal feePercent) => Mutate(next =>
    { next.Options = options; next.FeePercent = feePercent; });

    public QuantBacktestResult Backtest(long objectId)
    {
        lock (_gate) return QuantBacktest.Run(CachedHistory(objectId)
            ?? throw new InvalidOperationException("请先查询该物品的历史行情。"), _workspace.Options, _workspace.Budget, _workspace.FeePercent);
    }

    public long[] RefreshTargets(int grade, bool holdings)
    {
        lock (_gate) return (holdings
            ? _workspace.Trades.Where(t => !t.IsClosed).Select(t => t.ObjectId)
            : _workspace.Forecasts.Where(f => f.Grade == grade && f.IsCurrent(DateTimeOffset.Now)).SelectMany(f => f.Entries).Select(e => e.ObjectId))
            .Where(id => id > 0).Distinct().Order().ToArray();
    }

    public async Task RefreshManyAsync(IReadOnlyList<long> objectIds, string token, IProgress<string>? progress, CancellationToken ct)
    {
        var ids = objectIds.Distinct().ToArray();
        if (ids.Length > 200 || ids.Any(id => id <= 0)) throw new ArgumentException("物品查询列表无效。");
        if (!await _refresh.WaitAsync(0, ct)) throw new InvalidOperationException("行情正在刷新。");
        try
        {
            for (int i = 0; i < ids.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"正在查询行情 {i + 1}/{ids.Length}");
                var history = await _source.FetchHistoryAsync(ids[i], token, ct);
                ct.ThrowIfCancellationRequested();
                SaveHistory(history);
            }
        }
        finally { _refresh.Release(); }
    }

    private void SaveHistory(AmmoPriceHistory history) => Mutate(next =>
    {
        next.Histories.RemoveAll(h => h.ObjectId == history.ObjectId);
        next.Histories.Add(history);
        next.Histories = next.Histories.OrderByDescending(h => h.FetchedAt).Take(200).ToList();
    });

    public async Task<AmmoPriceHistory> RefreshHistoryAsync(long objectId, string token, CancellationToken ct)
    {
        if (!await _refresh.WaitAsync(0, ct)) throw new InvalidOperationException("数据正在刷新，请稍候。");
        try
        {
            var result = await _source.FetchHistoryAsync(objectId, token, ct);
            ct.ThrowIfCancellationRequested();
            SaveHistory(result);
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
                Histories = [.. _workspace.Histories], Options = _workspace.Options,
            };
            change(next);
            next.Validate();
            _store.Save(_path, next);
            _workspace = next;
        }
    }
}
