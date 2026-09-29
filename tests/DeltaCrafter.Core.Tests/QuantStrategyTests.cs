using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L3;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class QuantStrategyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 16, 0, 0, TimeSpan.FromHours(8));
    private static readonly AmmoLowPrice Item = new(35, 1222, ".300BLK_4", 4, 17, 99, "");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "delta-strategy-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    private static AmmoPriceHistory History(params decimal[] prices) => new(1222, Now,
        prices.Select((p, i) => new AmmoPricePoint($"t{i}", p, Now.AddMinutes(i - prices.Length + 1))).ToArray());

    [Theory]
    [InlineData("SMART")][InlineData("PROFIT")][InlineData("DIP")][InlineData("RANGE")]
    public void Rules_use_actual_history_and_budget_not_forecast_prices(string strategy)
    {
        var h = History(220, 200, 180, 100);
        var options = new QuantOptions(Strategy: strategy, Period: 3, MaxQuantity: 7);
        var candidate = QuantStrategies.Evaluate(Item with { Price = 1 }, h, options, 1050, 13, Now);
        Assert.True(candidate.Eligible, candidate.Reason);
        Assert.Equal(100, candidate.LatestPrice);
        Assert.Equal(7, candidate.Quantity);
        decimal target = strategy == "PROFIT" ? 220 : 160;
        Assert.Equal(target, candidate.TargetPrice);
        Assert.Equal((target * .87m - 100) * 7, candidate.NetProfit);
    }

    [Fact]
    public void Rebound_and_momentum_require_their_own_price_patterns()
    {
        var options = new QuantOptions(Period: 4);
        var rebound = History(300, 150, 100, 110);
        Assert.True(QuantStrategies.Evaluate(Item, rebound, options with { Strategy = "REBOUND" }, 10000, 0, Now).Eligible);
        var rise = History(1000, 100, 110, 120, 130);
        Assert.True(QuantStrategies.Evaluate(Item, rise, options with { Strategy = "RISE" }, 10000, 0, Now).Eligible);
        Assert.False(QuantStrategies.Evaluate(Item, History(100, 110, 120, 130), options with { Strategy = "DIP" }, 10000, 0, Now).Eligible);
    }

    [Fact]
    public void Missing_stale_future_or_wrong_item_quotes_never_qualify()
    {
        var options = new QuantOptions(Period: 3);
        Assert.False(QuantStrategies.Evaluate(Item, null, options, 1000, 13, Now).Eligible);
        var h = History(300, 200, 100);
        Assert.False(QuantStrategies.Evaluate(Item, h with { ObjectId = 99 }, options, 1000, 13, Now).Eligible);
        Assert.False(QuantStrategies.Evaluate(Item, h, options, 1000, 13, Now.AddMinutes(31)).Eligible);
        Assert.False(QuantStrategies.Evaluate(Item, h, options, 1000, 13, Now.AddMinutes(-1)).Eligible);
        Assert.False(QuantStrategies.Evaluate(Item, h with { Points = h.Points.Select(p => p with { ObservedAt = null }).ToArray() }, options, 1000, 13, Now).Eligible);
        Assert.False(QuantStrategies.Evaluate(Item, h, options, 99, 13, Now).Eligible);
        Assert.False(QuantStrategies.Evaluate(Item, h, options, 1000, 99, Now).Eligible);
    }

    [Fact]
    public void Position_does_not_use_pre_purchase_peak_or_forecast_as_current_value()
    {
        var h = History(900, 100, 125, 120);
        var trade = new AmmoTrade(Guid.NewGuid(), 35, "子弹", 4, 10, 100, Now.AddMinutes(-1), ObjectId: 1222);
        var result = QuantStrategies.Position(trade, h, new(TakeProfit: 0, StopLoss: 0, TrailingStop: 10), Now);
        Assert.Equal("持有观察", result.Signal); // Peak since purchase=125, not 900.
        Assert.Equal(200, result.FloatingProfit);
        Assert.Equal(1200, result.NetValue);
        var trailing = QuantStrategies.Position(trade, h, new(TakeProfit: 0, StopLoss: 0, TrailingStop: 3), Now);
        Assert.Equal("移动止盈提示", trailing.Signal);
        Assert.Null(QuantStrategies.Position(trade, h, new(), Now.AddHours(1)).FloatingProfit);
        Assert.Null(QuantStrategies.Position(trade with { OpenedAt = Now.AddSeconds(1) }, h, new(), Now.AddSeconds(1)).FloatingProfit);
    }

    [Fact]
    public void Risk_thresholds_use_net_after_tax_returns()
    {
        var trade = new AmmoTrade(Guid.NewGuid(), 35, "子弹", 4, 10, 100, Now.AddMinutes(-5), FeePercent: 13, ObjectId: 1222);
        Assert.Equal("止损提示", QuantStrategies.Position(trade, History(100, 90), new(), Now).Signal);
        Assert.Equal("持有观察", QuantStrategies.Position(trade, History(100, 110), new(), Now).Signal);
        Assert.Equal("止盈提示", QuantStrategies.Position(trade, History(100, 130), new(), Now).Signal);
    }

    [Fact]
    public void Backtest_uses_next_sample_fills_and_accounts_for_cash_and_fees()
    {
        var result = QuantBacktest.Run(History(150, 140, 130, 100, 115, 120, 110),
            new(Strategy: "DIP", Period: 3, MinimumReturn: 0, TakeProfit: 5, StopLoss: 50, TrailingStop: 0), 1000, 5);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(3, trade.BuyIndex);
        Assert.Equal(5, trade.SellIndex);
        Assert.Equal(100, trade.BuyPrice);
        Assert.Equal(120, trade.SellPrice);
        Assert.Equal(10, trade.Quantity);
        Assert.Equal(140, trade.NetProfit);
        Assert.Equal(1140, result.FinalCash);
        Assert.Equal(14, result.ReturnPercent);
        Assert.True(result.MaxDrawdownPercent >= 5); // immediate liquidation would pay the fee.
    }

    [Fact]
    public void Future_high_does_not_create_a_past_entry_signal()
    {
        var options = new QuantOptions(Strategy: "PROFIT", Period: 3, MinimumReturn: 20);
        var result = QuantBacktest.Run(History(100, 100, 100, 100, 100, 1000), options, 1000, 0);
        Assert.Empty(result.Trades);
        Assert.Equal(1000, result.FinalCash);
    }

    [Fact]
    public void Backtest_slippage_and_final_liquidation_are_included()
    {
        var result = QuantBacktest.Run(History(400, 300, 180, 100, 110),
            new(Strategy: "DIP", Period: 3, Slippage: 1, TakeProfit: 0, StopLoss: 0, TrailingStop: 0), 1000, 10);
        var trade = Assert.Single(result.Trades);
        Assert.Equal(101, trade.BuyPrice);
        Assert.Equal(108.9m, trade.SellPrice);
        Assert.Equal(9, trade.Quantity);
        Assert.Equal("样本结束", trade.Reason);
        Assert.Equal(1000 + (108.9m * .9m - 101) * 9, result.FinalCash);
        Assert.True(result.ReturnPercent < 0);
    }

    [Fact]
    public void Backtest_rejects_insufficient_samples_and_invalid_config()
    {
        Assert.Throws<ArgumentException>(() => QuantBacktest.Run(History(100, 101), new(), 1000, 13));
        Assert.Throws<ArgumentException>(() => QuantBacktest.Run(History(100, 101, 102, 103), new(Strategy: "AI"), 1000, 13));
        Assert.Throws<ArgumentException>(() => QuantBacktest.Run(History(100, 0, 102, 103), new(Period: 2), 1000, 13));
    }

    [Theory]
    [InlineData("09-29 周二 15:50分", "2026-09-29T15:50:00+08:00")]
    [InlineData("29 15:50分", "2026-09-29T15:50:00+08:00")]
    [InlineData("09-28 23:50", "2026-09-28T23:50:00+08:00")]
    public void History_source_timestamp_is_preserved(string label, string expected)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new { a = new[] { label }, b = new[] { 100 } } });
        Assert.Equal(DateTimeOffset.Parse(expected), AmmoMarketClient.ParseHistory(1222, json, Now).Points[0].ObservedAt);
    }

    [Theory]
    [InlineData("09-29 16:10", "09-29 16:20")]
    [InlineData("09-29 15:30", "09-29 15:00")]
    [InlineData("09-29 15:30", "09-29 15:30")]
    [InlineData("garbage", "09-29 15:30")]
    public void Bad_timestamps_do_not_become_fresh_quotes(string first, string last)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new { a = new[] { first, last }, b = new[] { 100, 110 } } });
        var history = AmmoMarketClient.ParseHistory(1222, json, Now);
        Assert.False(QuantStrategies.IsFresh(history, Now));
    }

    [Theory]
    [InlineData("2026-10-01T00:10:00+08:00", "30 23:50分", "2026-09-30T23:50:00+08:00")]
    [InlineData("2027-01-01T00:10:00+08:00", "12-31 23:50", "2026-12-31T23:50:00+08:00")]
    public void Source_time_crosses_month_and_year_boundaries(string fetched, string label, string expected)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { code = 0, data = new { a = new[] { label }, b = new[] { 100 } } });
        Assert.Equal(DateTimeOffset.Parse(expected), AmmoMarketClient.ParseHistory(1222, json, DateTimeOffset.Parse(fetched)).Points[0].ObservedAt);
    }

    [Fact]
    public void Backtest_and_live_signals_reject_unknown_or_reordered_history()
    {
        var h = History(300, 200, 100, 110, 120);
        var unknown = h with { Points = h.Points.Select(p => p with { ObservedAt = null }).ToArray() };
        var unordered = h with { Points = h.Points.Reverse().ToArray() };
        foreach (var invalid in new[] { unknown, unordered, h with { FetchedAt = Now.AddMinutes(-1) } })
        {
            Assert.False(QuantStrategies.IsFresh(invalid, Now));
            Assert.Throws<ArgumentException>(() => QuantBacktest.Run(invalid, new(Period: 3), 1000, 13));
        }
    }

    private sealed class Source : IAmmoMarketSource
    {
        public List<long> Requests { get; } = [];
        public long FailId;
        public Task<AmmoForecast> FetchLowPricesAsync(int grade, string token, CancellationToken ct) => Task.FromResult(new AmmoForecast(4, Now, AmmoForecast.ChinaDate(Now), [Item]));
        public Task<AmmoPriceHistory> FetchHistoryAsync(long id, string token, CancellationToken ct)
        {
            Requests.Add(id);
            if (id == FailId) throw new InvalidOperationException("Unavailable");
            return Task.FromResult(History(300, 200, 100) with { ObjectId = id });
        }
    }

    [Fact]
    public async Task Batch_deduplicates_stops_on_failure_and_preserves_completed_cache_across_restart()
    {
        var source = new Source { FailId = 2 };
        string path = Path.Combine(_dir, "quant.json");
        var coordinator = new QuantCoordinator(source, new(), path);
        coordinator.SaveAnalysisSettings(new(Strategy: "RANGE", Period: 3), 10);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshManyAsync([1, 1, 2, 3], "token", null, default));
        Assert.Equal(new long[] { 1, 2 }, source.Requests);
        Assert.NotNull(coordinator.CachedHistory(1));
        Assert.Null(coordinator.CachedHistory(2));
        var reopened = new QuantCoordinator(source, new(), path);
        Assert.NotNull(reopened.CachedHistory(1));
        Assert.Equal("RANGE", reopened.Snapshot(Now).Options!.Strategy);
        Assert.Equal(10, reopened.Preferences.FeePercent);
        Assert.Equal(2, source.Requests.Count); // reads and restart do not query.
    }

    [Fact]
    public async Task Old_ledger_recovers_object_id_and_partial_sale_keeps_identity()
    {
        string path = Path.Combine(_dir, "quant.json");
        var source = new Source();
        var coordinator = new QuantCoordinator(source, new(), path);
        await coordinator.RefreshAsync(4, "token", default);
        var id = Guid.NewGuid();
        coordinator.RecordPurchase(new(id, Item.Id, Item.Name, 4, 10, 100, Now));
        var reopened = new QuantCoordinator(source, new(), path);
        Assert.Equal(1222, reopened.Snapshot(Now).Trades[0].ObjectId);
        reopened.RecordSale(id, 4, 120, 13, Now.AddMinutes(1));
        Assert.All(reopened.Snapshot(Now).Trades, t => Assert.Equal(1222, t.ObjectId));
    }
}
