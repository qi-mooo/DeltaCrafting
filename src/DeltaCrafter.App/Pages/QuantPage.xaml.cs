using DeltaCrafter.App.Services;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L3;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace DeltaCrafter.App.Pages;

public sealed partial class QuantPage : Page
{
    private readonly AppHost _host = AppHost.Current;
    private QuantCoordinator? _quant;
    private CancellationTokenSource? _request;
    private bool _ready, _editing;
    private long _itemId;
    private long _objectId;
    private AmmoPriceHistory? _history;
    private string _name = "";
    private int _grade;
    private AmmoTrade? _trade;
    private int _generation;
    private QuantSnapshot? _snapshot;
    private sealed record Row(string Title, string Detail, string Value, string Extra, object Source);

    public QuantPage()
    {
        InitializeComponent();
        HourFilter.Items.Add("全部时段");
        for (int hour = 0; hour < 24; hour++) HourFilter.Items.Add($"{hour:00}:00");
        HourFilter.SelectedIndex = 0;
        try
        {
            _quant = _host.Quant;
            Budget.Value = (double)_quant.Preferences.Budget;
            Fee.Value = (double)_quant.Preferences.FeePercent;
            _ready = true;
            Reload();
        }
        catch (Exception ex) { Status.Text = ex.Message; RefreshButton.IsEnabled = false; }
    }

    private void Reload()
    {
        if (!_ready || _quant is null) return;
        _snapshot = _quant.Snapshot(DateTimeOffset.Now);
        var summary = _snapshot.Summary;
        OpenCost.Text = $"{summary.OpenCost:N0}";
        TodayProfit.Text = $"{summary.TodayProfit:+#,##0;-#,##0;0}";
        TotalProfit.Text = $"{summary.RealizedProfit:+#,##0;-#,##0;0}";
        TradeStats.Text = $"{summary.WinRate:0.#}% / {summary.MaxDrawdown:N0}";
        var forecast = _snapshot.Forecasts.FirstOrDefault(f => f.Grade == GradeFilter.SelectedIndex);
        DataTime.Text = forecast is null ? "数据帝 · 尚未查询" :
            $"数据帝 · {forecast.FetchedAt.ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm} 更新" +
            (forecast.IsCurrent(DateTimeOffset.Now) ? "" : " · 已过期");
        string search = Search.Text.Trim();
        bool Match(string name) => name.Contains(search, StringComparison.OrdinalIgnoreCase);
        IEnumerable<Row> rows;
        int view = Math.Max(0, Views.SelectedIndex);
        GradeFilter.IsEnabled = HourFilter.IsEnabled = SortFilter.IsEnabled = view == 0 && !QueryProgress.IsActive;
        RefreshButton.Visibility = view == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (view == 0)
        {
            var entries = (forecast?.Entries ?? []).Where(e => Match(e.Label)
                && (HourFilter.SelectedIndex <= 0 || e.Hour == HourFilter.SelectedIndex - 1));
            if (SortFilter.SelectedIndex == 3)
                entries = entries.GroupBy(e => e.Id).Select(g => g.OrderBy(e => e.Price).ThenBy(e => e.Hour).First());
            entries = SortFilter.SelectedIndex switch
            { 1 or 3 => entries.OrderBy(e => e.Price).ThenBy(e => e.Hour), 2 => entries.OrderBy(e => e.Label).ThenBy(e => e.Hour), _ => entries.OrderBy(e => e.Hour).ThenBy(e => e.Price) };
            decimal budget = Number(Budget);
            rows = entries.Select(e => new Row(e.Label, $"预测时段 {e.TimeLabel} · 预算可买 {QuantAccounting.AffordableQuantity(budget, e.Price):N0} 发",
                e.PriceLabel, "预测低价 / 发", e));
            ListHeading.Text = "今日低价预测 · 北京时间";
            Status.Text = forecast is null ? "尚未查询所选等级" : forecast.Entries.Count == 0 ? "所选等级暂无低价预测" :
                forecast.IsCurrent(DateTimeOffset.Now) ? $"{forecast.ForecastDate:yyyy-MM-dd} · {forecast.Entries.Count} 条预测" : "预测已过期，请刷新当日数据";
        }
        else if (view == 1)
        {
            rows = _snapshot.Watchlist.Where(w => Match(w.Label)).Select(w => new Row(w.Label,
                $"买入 ≤ {w.BuyLimit:N0} · 卖出目标 {w.SellTarget:N0} · {w.Quantity:N0} 发",
                $"{w.NetProfit:+#,##0;-#,##0;0}", $"目标税后 {w.NetReturnPercent:0.#}%", w));
            ListHeading.Text = "自选计划 · 目标收益";
            Status.Text = $"{_snapshot.Watchlist.Count} 项自选";
        }
        else
        {
            rows = _snapshot.Trades.Where(t => t.IsClosed == (view == 3) && Match(t.Label))
                .OrderByDescending(t => t.ClosedAt ?? t.OpenedAt).Select(t => new Row(t.Label,
                    $"{(t.ClosedAt ?? t.OpenedAt).ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm} · {t.Quantity:N0} 发 · 买入 {t.BuyPrice:N0}" +
                    (t.IsClosed ? $" · 卖出 {t.SellPrice:N0}" : ""),
                    t.IsClosed ? $"{t.Profit:+#,##0;-#,##0;0}" : $"{t.Cost:N0}", t.IsClosed ? "已实现收益" : "持仓成本", t));
            ListHeading.Text = view == 3 ? "成交记录 · 实际价格扣除卖出手续费" : "持仓 · 实际买入成本";
            Status.Text = view == 3 ? $"{summary.ClosedCount} 笔已完成交易" : $"{summary.OpenCount} 笔持仓";
        }
        Rows.ItemsSource = rows.ToArray();
        HideDetail();
    }

    private async void RefreshForecast(object sender, RoutedEventArgs e)
    {
        if (_quant is null || QueryProgress.IsActive) return;
        int generation = ++_generation;
        _request?.Dispose();
        _request = CancellationTokenSource.CreateLinkedTokenSource(_host.AppStopToken);
        int grade = GradeFilter.SelectedIndex;
        QueryProgress.IsActive = true;
        RefreshButton.IsEnabled = GradeFilter.IsEnabled = false;
        Status.Text = $"正在查询 {grade} 级子弹…";
        try
        {
            await _quant.RefreshAsync(grade, _host.Settings.ManufactureApi.Token, _request.Token);
            if (generation == _generation) Reload();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation) Status.Text = ex.Message; }
        finally
        {
            if (generation == _generation)
            {
                QueryProgress.IsActive = false;
                RefreshButton.IsEnabled = true;
                GradeFilter.IsEnabled = HourFilter.IsEnabled = SortFilter.IsEnabled = Views.SelectedIndex == 0;
            }
        }
    }

    private void SelectRow(object sender, SelectionChangedEventArgs e)
    {
        if (Rows.SelectedItem is not Row row || _quant is null) return;
        _editing = true;
        _trade = row.Source as AmmoTrade;
        decimal buy, sell;
        int quantity;
        ItemImage.Source = null;
        _objectId = 0;
        if (row.Source is AmmoLowPrice quote)
        {
            (_itemId, _name, _grade) = (quote.Id, quote.Name, quote.Grade);
            _objectId = quote.ObjectId;
            var watch = _snapshot?.Watchlist.FirstOrDefault(w => w.Id == _itemId);
            buy = watch?.BuyLimit ?? quote.Price;
            sell = watch?.SellTarget ?? 0;
            quantity = watch?.Quantity ?? Math.Max(1, QuantAccounting.AffordableQuantity(Number(Budget), buy));
            Fee.Value = (double)(watch?.FeePercent ?? _quant.Preferences.FeePercent);
            ItemInfo.Text = $"预测时段 {quote.TimeLabel}\n预测低价 {quote.Price:N0} / 发";
            if (quote.ImageUrl.Length > 0) ItemImage.Source = new BitmapImage(new Uri(quote.ImageUrl));
        }
        else if (row.Source is AmmoWatchItem watch)
        {
            (_itemId, _name, _grade) = (watch.Id, watch.Name, watch.Grade);
            buy = watch.BuyLimit; sell = watch.SellTarget; quantity = watch.Quantity;
            Fee.Value = (double)watch.FeePercent;
            ItemInfo.Text = "自选计划";
        }
        else if (_trade is { } trade)
        {
            (_itemId, _name, _grade) = (trade.ItemId, trade.Name, trade.Grade);
            buy = trade.BuyPrice; sell = trade.SellPrice ?? 0; quantity = trade.Quantity;
            Fee.Value = (double)trade.FeePercent;
            ItemInfo.Text = $"实际买入 {trade.OpenedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm}";
        }
        else { _editing = false; return; }
        if (_objectId == 0)
            _objectId = _snapshot?.Forecasts.SelectMany(f => f.Entries).FirstOrDefault(q => q.Id == _itemId)?.ObjectId ?? 0;
        HistoryButton.IsEnabled = _objectId > 0 && !QueryProgress.IsActive;
        ShowHistory(_quant.CachedHistory(_objectId));
        ItemTitle.Text = AmmoLabels.Format(_name, _grade);
        BuyPrice.Value = (double)buy;
        SellPrice.Value = sell > 0 ? (double)sell : double.NaN;
        Quantity.Maximum = _trade?.Quantity ?? 1_000_000;
        Quantity.Value = quantity;
        BuyPrice.IsEnabled = _trade is null;
        SellPrice.IsEnabled = Quantity.IsEnabled = Fee.IsEnabled = _trade?.IsClosed != true;
        SaveWatchButton.Visibility = PurchaseButton.Visibility = _trade is null ? Visibility.Visible : Visibility.Collapsed;
        SaleButton.Visibility = _trade is { IsClosed: false } ? Visibility.Visible : Visibility.Collapsed;
        RemoveWatchButton.Visibility = row.Source is AmmoWatchItem ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = new GridLength(280);
        DetailPanel.Visibility = Visibility.Visible;
        _editing = false;
        UpdateEstimate();
    }

    private static decimal Number(NumberBox box) => double.IsFinite(box.Value) ? (decimal)box.Value : 0;
    private AmmoWatchItem ReadWatch()
    {
        if (!double.IsFinite(Fee.Value)) throw new ArgumentException("请填写手续费比例。");
        decimal quantity = Number(Quantity);
        if (quantity != decimal.Truncate(quantity)) throw new ArgumentException("数量必须是整数。");
        var item = new AmmoWatchItem(_itemId, _name, _grade, Number(BuyPrice), Number(SellPrice), (int)quantity, Number(Fee));
        item.Validate();
        return item;
    }
    private void UpdateEstimate()
    {
        if (!_ready || _editing) return;
        try
        {
            var item = ReadWatch();
            Estimate.Text = $"成本 {item.Cost:N0}\n{(_trade?.IsClosed == true ? "已实现收益" : "目标税后收益")} {item.NetProfit:+#,##0;-#,##0;0} ({item.NetReturnPercent:0.#}%)";
        }
        catch (ArgumentException) { Estimate.Text = "卖出目标未设置"; }
    }
    private void Calculate(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdateEstimate();

    private async void RefreshHistory(object sender, RoutedEventArgs e)
    {
        if (_quant is null || _objectId <= 0 || QueryProgress.IsActive) return;
        long objectId = _objectId;
        int generation = ++_generation;
        _request?.Dispose();
        _request = CancellationTokenSource.CreateLinkedTokenSource(_host.AppStopToken);
        QueryProgress.IsActive = true;
        HistoryButton.IsEnabled = RefreshButton.IsEnabled = false;
        Status.Text = "正在查询历史价格…";
        try
        {
            var history = await _quant.RefreshHistoryAsync(objectId, _host.Settings.ManufactureApi.Token, _request.Token);
            if (generation == _generation && objectId == _objectId)
            { ShowHistory(history); Status.Text = "历史与指标已更新"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation) Status.Text = ex.Message; }
        finally
        {
            if (generation == _generation)
            { QueryProgress.IsActive = false; RefreshButton.IsEnabled = true; HistoryButton.IsEnabled = _objectId > 0; }
        }
    }

    private void ShowHistory(AmmoPriceHistory? history)
    {
        _history = history;
        HistoryPanel.Visibility = history is null ? Visibility.Collapsed : Visibility.Visible;
        if (history is null) return;
        HistoryTime.Text = history.Points.Count == 0 ? "暂无历史价格" :
            $"{history.Points[0].TimeLabel} — {history.Points[^1].TimeLabel}\n查询于 {history.FetchedAt.ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm}";
        var metrics = QuantIndicators.Calculate(history.Points);
        Indicators.Text = metrics is null ? $"{history.Points.Count} 个有效采样，至少需要 20 个采样计算指标" :
            $"最近 {metrics.Samples} 个采样\n最新 {metrics.Last:N0} · MA {metrics.Mean:N0} · EMA {metrics.Ema:N0}\n" +
            $"RSI {metrics.Rsi:0.#} · 区间位置 {metrics.PositionPercent:0.#}%\n" +
            $"低 {metrics.Low:N0} / 高 {metrics.High:N0}\n涨跌 {metrics.ChangePercent:+0.0;-0.0;0}% · 波动 {metrics.Volatility:0.#}";
        DrawChart();
    }
    private void ChartSizeChanged(object sender, SizeChangedEventArgs e) => DrawChart();
    private void DrawChart()
    {
        if (PriceLine is null) return;
        var vertices = new PointCollection();
        var points = _history?.Points;
        if (points is { Count: > 1 })
        {
            decimal low = points.Min(p => p.Price), high = points.Max(p => p.Price);
            double width = Math.Max(1, HistoryChart.ActualWidth - 8), height = HistoryChart.Height - 8;
            for (int i = 0; i < points.Count; i++)
                vertices.Add(new Windows.Foundation.Point(4 + width * i / (points.Count - 1),
                    4 + height * (high == low ? .5 : 1 - (double)((points[i].Price - low) / (high - low)))));
        }
        PriceLine.Points = vertices;
    }
    private void SaveWatch(object sender, RoutedEventArgs e) => Execute(() =>
    {
        var item = ReadWatch();
        _quant!.SaveWatch(item);
        _quant.SavePreferences(Number(Budget), item.FeePercent);
    }, "自选计划已保存");

    private async void RecordPurchase(object sender, RoutedEventArgs e)
    {
        try
        {
            decimal quantity = Number(Quantity), price = Number(BuyPrice), fee = Number(Fee);
            if (!double.IsFinite(Fee.Value)) throw new ArgumentException("请填写手续费比例。");
            if (quantity != decimal.Truncate(quantity)) throw new ArgumentException("数量必须是整数。");
            var trade = new AmmoTrade(Guid.NewGuid(), _itemId, _name, _grade, (int)quantity, price, DateTimeOffset.Now, FeePercent: fee);
            trade.Validate();
            if (!await Confirm("记录实际买入", $"{trade.Label}\n{trade.Quantity:N0} 发 × {price:N0}\n实际支出 {trade.Cost:N0}")) return;
            Execute(() => _quant!.RecordPurchase(trade), "买入记录已保存");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async void RecordSale(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_trade is null) return;
            var trade = _trade;
            var item = ReadWatch();
            if (item.Quantity > trade.Quantity) throw new ArgumentException("卖出数量超过持仓。");
            if (!await Confirm("记录实际卖出", $"{trade.Label}\n{item.Quantity:N0} 发 × {item.SellTarget:N0}\n税后收益 {item.NetProfit:+#,##0;-#,##0;0}")) return;
            Execute(() => _quant!.RecordSale(trade.Id, item.Quantity, item.SellTarget, item.FeePercent, DateTimeOffset.Now), "卖出记录已保存");
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async Task<bool> Confirm(string title, string text) => await new ContentDialog
    { XamlRoot = XamlRoot, Title = title, Content = text, PrimaryButtonText = "确认记录", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    private void RemoveWatch(object sender, RoutedEventArgs e) => Execute(() => _quant!.RemoveWatch(_itemId), "已移除自选");
    private void Execute(Action action, string success)
    {
        try { action(); Reload(); Status.Text = success; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void PreferenceChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready || _quant is null) return;
        try { _quant.SavePreferences(Number(Budget), _quant.Preferences.FeePercent); Reload(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void ChangeView(object sender, SelectionChangedEventArgs e) => Reload();
    private void FilterChanged(object sender, SelectionChangedEventArgs e) => Reload();
    private void SearchChanged(object sender, TextChangedEventArgs e) => Reload();
    private void HideDetail() { DetailPanel.Visibility = Visibility.Collapsed; DetailColumn.Width = new GridLength(0); _trade = null; _objectId = 0; }
    private void CloseDetail(object sender, RoutedEventArgs e) { Rows.SelectedItem = null; HideDetail(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    { ++_generation; _request?.Cancel(); _request?.Dispose(); _request = null; base.OnNavigatedFrom(e); }
}
