namespace DeltaCrafter.Core.L0;

public sealed record AmmoLowPrice(long Id, long ObjectId, string Name, int Grade,
    int Hour, decimal Price, string ImageUrl)
{
    public string Label => AmmoLabels.Format(Name, Grade);
    public string TimeLabel => $"{Hour:00}:00";
    public string PriceLabel => $"{Price:N0}";
}

public sealed record AmmoForecast(int Grade, DateTimeOffset FetchedAt, DateOnly ForecastDate,
    IReadOnlyList<AmmoLowPrice> Entries)
{
    public bool IsCurrent(DateTimeOffset now) => ForecastDate == ChinaDate(now);
    public static DateOnly ChinaDate(DateTimeOffset now) => DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(8)).DateTime);
}

/// <summary>行情适配边界。预测报价不能用作实际成交价。</summary>
public interface IAmmoMarketSource
{
    Task<AmmoForecast> FetchLowPricesAsync(int grade, string token, CancellationToken ct);
    Task<AmmoPriceHistory> FetchHistoryAsync(long objectId, string token, CancellationToken ct);
}

public sealed record AmmoPricePoint(string TimeLabel, decimal Price);
public sealed record AmmoPriceHistory(long ObjectId, DateTimeOffset FetchedAt, IReadOnlyList<AmmoPricePoint> Points);

public sealed record AmmoWatchItem(long Id, string Name, int Grade, decimal BuyLimit,
    decimal SellTarget, int Quantity, decimal FeePercent)
{
    public string Label => AmmoLabels.Format(Name, Grade);
    public decimal Cost => BuyLimit * Quantity;
    public decimal NetProfit => (SellTarget * (1 - FeePercent / 100m) - BuyLimit) * Quantity;
    public decimal NetReturnPercent => Cost == 0 ? 0 : NetProfit / Cost * 100m;
    public void Validate()
    {
        if (Id <= 0 || string.IsNullOrWhiteSpace(Name) || Name.Length > 160 || Name.Any(char.IsControl)
            || Grade is < 0 or > 6 || BuyLimit is <= 0 or > 1_000_000_000 || SellTarget is <= 0 or > 1_000_000_000
            || Quantity is < 1 or > 1_000_000 || FeePercent is < 0 or >= 100)
            throw new ArgumentException("请检查物品、买入限价、卖出目标、数量和手续费。");
    }
}

public sealed record AmmoTrade(Guid Id, long ItemId, string Name, int Grade, int Quantity,
    decimal BuyPrice, DateTimeOffset OpenedAt, decimal? SellPrice = null,
    decimal FeePercent = 0, DateTimeOffset? ClosedAt = null)
{
    public string Label => AmmoLabels.Format(Name, Grade);
    public bool IsClosed => ClosedAt.HasValue;
    public decimal Cost => BuyPrice * Quantity;
    public decimal? Profit => IsClosed && SellPrice.HasValue
        ? (SellPrice.Value * (1 - FeePercent / 100m) - BuyPrice) * Quantity : null;
    public void Validate()
    {
        new AmmoWatchItem(ItemId, Name, Grade, BuyPrice, SellPrice ?? BuyPrice, Quantity, FeePercent).Validate();
        if (Id == Guid.Empty || OpenedAt == default || SellPrice.HasValue != ClosedAt.HasValue || ClosedAt < OpenedAt)
            throw new ArgumentException("交易记录的时间或成交价格无效。");
    }
}

public sealed class QuantWorkspace
{
    public int SchemaVersion { get; set; } = 1;
    public List<AmmoForecast> Forecasts { get; set; } = [];
    public List<AmmoWatchItem> Watchlist { get; set; } = [];
    public List<AmmoTrade> Trades { get; set; } = [];
    public decimal Budget { get; set; } = 100_000;
    public decimal FeePercent { get; set; } = 13;

    public void Validate()
    {
        if (SchemaVersion != 1 || Budget is <= 0 or > 1_000_000_000 || FeePercent is < 0 or >= 100
            || Forecasts is null || Watchlist is null || Trades is null
            || Forecasts.Count > 7 || Forecasts.Select(f => f.Grade).Distinct().Count() != Forecasts.Count
            || Watchlist.Select(w => w.Id).Distinct().Count() != Watchlist.Count
            || Trades.Select(t => t.Id).Distinct().Count() != Trades.Count)
            throw new ArgumentException("量化工作区数据无效，请检查 quant.json。");
        foreach (var item in Watchlist) item.Validate();
        foreach (var trade in Trades) trade.Validate();
        foreach (var forecast in Forecasts)
        {
            if (forecast.Grade is < 0 or > 6 || forecast.Entries is null || forecast.Entries.Count > 4800
                || forecast.FetchedAt == default || forecast.ForecastDate == default)
                throw new ArgumentException("子弹预测缓存无效。");
            foreach (var row in forecast.Entries)
                if (row.Grade != forecast.Grade || row.Id <= 0 || row.ObjectId <= 0 || row.Hour is < 0 or > 23
                    || row.Price is <= 0 or > 1_000_000_000 || string.IsNullOrWhiteSpace(row.Name))
                    throw new ArgumentException("子弹预测缓存无效。");
        }
    }
}

public sealed record QuantSummary(decimal OpenCost, decimal RealizedProfit, decimal TodayProfit,
    decimal WinRate, decimal MaxDrawdown, int ClosedCount, int OpenCount);

public static class QuantAccounting
{
    public static QuantSummary Summarize(IEnumerable<AmmoTrade> trades, DateTimeOffset now)
    {
        var all = trades.ToArray();
        var closed = all.Where(t => t.IsClosed).OrderBy(t => t.ClosedAt).ThenBy(t => t.Id).ToArray();
        decimal equity = 0, peak = 0, drawdown = 0;
        foreach (var trade in closed)
        {
            equity += trade.Profit!.Value;
            peak = Math.Max(peak, equity);
            drawdown = Math.Max(drawdown, peak - equity);
        }
        return new(all.Where(t => !t.IsClosed).Sum(t => t.Cost), equity,
            closed.Where(t => AmmoForecast.ChinaDate(t.ClosedAt!.Value) == AmmoForecast.ChinaDate(now)).Sum(t => t.Profit!.Value),
            closed.Length == 0 ? 0 : closed.Count(t => t.Profit > 0) * 100m / closed.Length,
            drawdown, closed.Length, all.Count(t => !t.IsClosed));
    }

    public static int AffordableQuantity(decimal budget, decimal price) => budget <= 0 || price <= 0
        ? 0 : (int)Math.Min(1_000_000, decimal.Floor(budget / price));
}

/// <summary>后续设备端使用相同快照；读取快照不会触发收费请求。</summary>
public sealed record QuantSnapshot(int SchemaVersion, IReadOnlyList<AmmoForecast> Forecasts,
    IReadOnlyList<AmmoWatchItem> Watchlist, IReadOnlyList<AmmoTrade> Trades, QuantSummary Summary);

public static class AmmoLabels
{
    public static string Format(string name, int grade) => $"{grade}级 " +
        (name == $".300BLK_{grade}" ? ".300BLK" : name);
}
