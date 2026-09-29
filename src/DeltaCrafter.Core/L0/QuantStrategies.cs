// Strategy scoring adapted from leitingquant/WindowSpy/MarketService.cs (MIT).
// Uses the returned minute-history interval, not the upstream seven-day catalog.
namespace DeltaCrafter.Core.L0;

public sealed record QuantOptions(string Strategy = "SMART", decimal MinimumReturn = 3,
    int MaxQuantity = 999, decimal TakeProfit = 10, decimal StopLoss = 20,
    decimal TrailingStop = 3, decimal Slippage = 0, int Period = 20)
{
    public static readonly string[] Strategies = ["SMART", "PROFIT", "RISE", "DIP", "REBOUND", "RANGE"];
    public void Validate()
    {
        if (!Strategies.Contains(Strategy) || MinimumReturn is < 0 or > 1000 || MaxQuantity is < 1 or > 1_000_000
            || TakeProfit is < 0 or > 1000 || StopLoss is < 0 or > 100 || TrailingStop is < 0 or > 100
            || Slippage is < 0 or > 20 || Period is < 2 or > 240)
            throw new ArgumentException("量化参数无效，请检查策略、数量和百分比。");
    }
    public static string Label(string strategy) => strategy switch
    { "PROFIT" => "利润优先", "RISE" => "动量追涨", "DIP" => "低位抄底", "REBOUND" => "超跌反弹", "RANGE" => "区间套利", _ => "综合选品" };
}

public sealed record QuantCandidate(AmmoLowPrice Item, bool Eligible, string Reason,
    decimal? LatestPrice = null, decimal? TargetPrice = null, decimal? NetReturn = null,
    int Quantity = 0, decimal? NetProfit = null, decimal? Score = null, DateTimeOffset? QuoteAt = null);

public sealed record QuantPosition(Guid TradeId, decimal? Price, decimal? NetValue, decimal? FloatingProfit,
    decimal? ReturnPercent, string Signal, DateTimeOffset? QuoteAt);

public static class QuantStrategies
{
    public static readonly TimeSpan QuoteLifetime = TimeSpan.FromMinutes(30);

    public static bool HasChronologicalPoints(AmmoPriceHistory? history) => history is { Points.Count: > 0 }
        && history.Points.All(p => p.ObservedAt.HasValue && p.ObservedAt <= history.FetchedAt)
        && !history.Points.Zip(history.Points.Skip(1)).Any(p => p.First.ObservedAt >= p.Second.ObservedAt);

    public static bool IsFresh(AmmoPriceHistory? history, DateTimeOffset now) =>
        HasChronologicalPoints(history) && history!.Points[^1].ObservedAt is { } time
        && time <= now && now - time <= QuoteLifetime && history.FetchedAt <= now;

    public static QuantCandidate Evaluate(AmmoLowPrice item, AmmoPriceHistory? history,
        QuantOptions options, decimal budget, decimal fee, DateTimeOffset now)
    {
        options.Validate();
        ValidateMoney(budget, fee);
        if (history is null || history.ObjectId != item.ObjectId || history.Points.Count == 0)
            return new(item, false, "尚未查询历史行情");
        var last = history.Points[^1];
        if (!IsFresh(history, now)) return new(item, false, "行情过期或缺少报价时间，请手动刷新", LatestPrice: last.Price, QuoteAt: last.ObservedAt);
        var metrics = QuantIndicators.Calculate(history.Points, options.Period);
        if (metrics is null) return new(item, false, $"历史不足 {options.Period} 个有效采样", LatestPrice: last.Price, QuoteAt: last.ObservedAt);
        decimal low = history.Points.Min(p => p.Price), high = history.Points.Max(p => p.Price);
        decimal mid = (low + high) / 2, target = options.Strategy == "PROFIT" ? high : mid;
        decimal buy = last.Price * (1 + options.Slippage / 100);
        decimal net = target * (1 - options.Slippage / 100) * (1 - fee / 100) - buy;
        decimal pct = net / buy * 100, dip = (mid - last.Price) / last.Price * 100;
        decimal range = (high - low) / low * 100;
        int quantity = Math.Min(options.MaxQuantity, QuantAccounting.AffordableQuantity(budget, buy));
        decimal score = options.Strategy switch
        {
            "PROFIT" => pct, "RISE" => metrics.ChangePercent,
            "DIP" => dip, "REBOUND" => dip - metrics.ChangePercent,
            "RANGE" => range, _ => quantity * net,
        };
        bool signal = EntrySignal(history.Points, options);
        string reason = quantity == 0 ? "预算不足一发" : pct < options.MinimumReturn ? "税后目标空间未达门槛"
            : !signal ? "尚未满足策略入场条件" : $"{QuantOptions.Label(options.Strategy)}条件满足";
        return new(item, quantity > 0 && pct >= options.MinimumReturn && signal, reason,
            last.Price, target, pct, quantity, quantity * net, score, last.ObservedAt);
    }

    // Shared by screening and backtest; only the samples supplied by the caller are visible.
    public static bool EntrySignal(IReadOnlyList<AmmoPricePoint> past, QuantOptions options)
    {
        var m = QuantIndicators.Calculate(past, options.Period);
        if (m is null) return false;
        return options.Strategy switch
        {
            "PROFIT" => m.Last < past.Max(p => p.Price),
            "RISE" => m.ChangePercent > 0 && m.Last > m.Mean && past[^1].Price > past[^2].Price,
            "DIP" => m.Rsi <= 35 && m.PositionPercent <= 25,
            "REBOUND" => m.Last < m.Mean && past[^1].Price > past[^2].Price && m.Rsi < 50,
            "RANGE" => m.High > m.Low && m.PositionPercent <= 25,
            _ => m.Last < m.Mean && m.Rsi < 50 && m.PositionPercent < 50,
        };
    }

    public static QuantPosition Position(AmmoTrade trade, AmmoPriceHistory? history, QuantOptions options, DateTimeOffset now)
    {
        trade.Validate(); options.Validate();
        if (trade.IsClosed || !IsFresh(history, now) || trade.ObjectId <= 0 || history!.ObjectId != trade.ObjectId
            || history.Points[^1].ObservedAt < trade.OpenedAt)
            return new(trade.Id, null, null, null, null, "待查询持仓行情", history?.Points.LastOrDefault()?.ObservedAt);
        decimal price = history.Points[^1].Price;
        decimal netValue = price * (1 - trade.FeePercent / 100) * trade.Quantity;
        decimal pct = (netValue - trade.Cost) / trade.Cost * 100;
        // Never use pre-purchase highs as a trailing-stop peak.
        decimal peak = history.Points.Where(p => p.ObservedAt >= trade.OpenedAt).Max(p => p.Price);
        decimal peakPct = (peak * (1 - trade.FeePercent / 100) - trade.BuyPrice) / trade.BuyPrice * 100;
        string signal = ExitSignal(pct, peakPct, options) ?? "持有观察";
        return new(trade.Id, price, netValue, netValue - trade.Cost, pct, signal, history.Points[^1].ObservedAt);
    }

    public static string? ExitSignal(decimal netReturn, decimal peakReturn, QuantOptions options) =>
        options.StopLoss > 0 && netReturn <= -options.StopLoss ? "止损提示" :
        options.TakeProfit > 0 && netReturn >= options.TakeProfit ? "止盈提示" :
        options.TrailingStop > 0 && peakReturn > 0 && netReturn > 0 && peakReturn - netReturn >= options.TrailingStop
            ? "移动止盈提示" : null;

    internal static void ValidateMoney(decimal budget, decimal fee)
    {
        if (budget is <= 0 or > 1_000_000_000 || fee is < 0 or >= 100) throw new ArgumentException("预算或手续费无效。");
    }
}
