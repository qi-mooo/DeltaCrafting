namespace DeltaCrafter.Core.L0;

public sealed record QuantBacktestTrade(int BuyIndex, int SellIndex, string BuyTime, string SellTime,
    decimal BuyPrice, decimal SellPrice, int Quantity, decimal NetProfit, string Reason);
public sealed record QuantBacktestResult(int Samples, string Begin, string End, decimal InitialCash,
    decimal FinalCash, decimal ReturnPercent, decimal WinRate, decimal MaxDrawdownPercent,
    decimal? ProfitFactor, IReadOnlyList<QuantBacktestTrade> Trades);

/// <summary>单品历史模拟：已知样本形成信号，下一有效样本成交；卖出手续费、双边滑点、整数数量。</summary>
public static class QuantBacktest
{
    public static QuantBacktestResult Run(AmmoPriceHistory history, QuantOptions options, decimal budget, decimal fee)
    {
        options.Validate(); QuantStrategies.ValidateMoney(budget, fee);
        var points = history.Points;
        if (points.Count < options.Period + 2 || points.Count > 1440 || points.Any(p => p.Price <= 0))
            throw new ArgumentException($"回测至少需要 {options.Period + 2} 个有效采样。");
        if (!QuantStrategies.HasChronologicalPoints(history))
            throw new ArgumentException("行情缺少有效时间或采样顺序异常，无法进行历史回测。");
        decimal cash = budget, peakEquity = budget, maxDrawdown = 0, buy = 0, peakReturn = 0;
        int quantity = 0, buyIndex = -1;
        bool pendingBuy = false;
        string? pendingSell = null;
        var trades = new List<QuantBacktestTrade>();
        var past = new List<AmmoPricePoint>();
        decimal SellPrice(decimal price) => price * (1 - options.Slippage / 100);
        void Close(int i, string reason)
        {
            decimal sell = SellPrice(points[i].Price);
            decimal proceeds = sell * (1 - fee / 100) * quantity;
            cash += proceeds;
            trades.Add(new(buyIndex, i, points[buyIndex].TimeLabel, points[i].TimeLabel,
                buy, sell, quantity, proceeds - buy * quantity, reason));
            quantity = 0; pendingSell = null;
        }
        for (int i = 0; i < points.Count; i++)
        {
            bool sold = false;
            if (pendingSell is not null && quantity > 0) { Close(i, pendingSell); sold = true; }
            if (pendingBuy)
            {
                pendingBuy = false;
                buy = points[i].Price * (1 + options.Slippage / 100);
                quantity = Math.Min(options.MaxQuantity, QuantAccounting.AffordableQuantity(cash, buy));
                cash -= buy * quantity; buyIndex = i; peakReturn = 0;
            }
            decimal value = SellPrice(points[i].Price) * (1 - fee / 100) * quantity;
            decimal equity = cash + value;
            peakEquity = Math.Max(peakEquity, equity);
            maxDrawdown = Math.Max(maxDrawdown, (peakEquity - equity) / peakEquity * 100);
            past.Add(points[i]);
            if (i == points.Count - 1)
            {
                if (quantity > 0) Close(i, "样本结束");
                break;
            }
            if (quantity > 0)
            {
                decimal pct = (value - buy * quantity) / (buy * quantity) * 100;
                peakReturn = Math.Max(peakReturn, pct);
                pendingSell = QuantStrategies.ExitSignal(pct, peakReturn, options);
            }
            else if (!sold && i < points.Count - 2 && past.Count >= options.Period)
            {
                // Evaluate only this prefix, never the full future interval.
                decimal target = options.Strategy == "PROFIT" ? past.Max(p => p.Price)
                    : (past.Max(p => p.Price) + past.Min(p => p.Price)) / 2;
                decimal entry = points[i].Price * (1 + options.Slippage / 100);
                decimal expected = (SellPrice(target) * (1 - fee / 100) - entry) / entry * 100;
                pendingBuy = expected >= options.MinimumReturn && QuantStrategies.EntrySignal(past, options);
            }
        }
        decimal profit = trades.Where(t => t.NetProfit > 0).Sum(t => t.NetProfit);
        decimal loss = -trades.Where(t => t.NetProfit < 0).Sum(t => t.NetProfit);
        return new(points.Count, points[0].TimeLabel, points[^1].TimeLabel, budget, cash,
            (cash - budget) / budget * 100, trades.Count == 0 ? 0 : trades.Count(t => t.NetProfit > 0) * 100m / trades.Count,
            maxDrawdown, loss == 0 ? null : profit / loss, trades);
    }
}
