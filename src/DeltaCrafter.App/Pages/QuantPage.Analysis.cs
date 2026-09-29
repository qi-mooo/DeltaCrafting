using DeltaCrafter.Core.L0;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaCrafter.App.Pages;

public sealed partial class QuantPage
{
    private void StrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _quant is null || StrategyFilter.SelectedIndex < 0) return;
        Execute(() => _quant.SaveOptions((_quant.Snapshot(DateTimeOffset.Now).Options ?? new()) with
        { Strategy = QuantOptions.Strategies[StrategyFilter.SelectedIndex] }), "策略已保存，使用已缓存行情重新计算");
    }

    private async void RefreshBatch(object sender, RoutedEventArgs e)
    {
        if (!_ready || _quant is null || QueryProgress.IsActive) return;
        var ids = _quant.RefreshTargets(GradeFilter.SelectedIndex, Views.SelectedIndex == 2);
        if (ids.Length == 0) { Status.Text = "没有可查询的物品，请先更新低价预测目录"; return; }
        int generation = ++_generation;
        _request?.Dispose();
        _request = CancellationTokenSource.CreateLinkedTokenSource(_host.AppStopToken);
        SetQueryState(true);
        string message;
        try
        {
            var progress = new Progress<string>(value => { if (generation == _generation) Status.Text = value; });
            await _quant.RefreshManyAsync(ids, _host.Settings.ManufactureApi.Token, progress, _request.Token);
            message = $"{ids.Length} 项行情已刷新";
        }
        catch (OperationCanceledException) { message = "查询已取消，已完成结果保留"; }
        catch (Exception ex) { message = ex.Message + " 已完成结果保留，剩余查询已停止。"; }
        finally
        {
            if (generation == _generation)
            {
                SetQueryState(false);
                Reload();
            }
        }
        if (generation == _generation) Status.Text = message;
    }

    private void CancelRefresh(object sender, RoutedEventArgs e) => _request?.Cancel();

    private async void EditOptions(object sender, RoutedEventArgs e)
    {
        if (_quant is null || !_ready) return;
        var options = _quant.Snapshot(DateTimeOffset.Now).Options ?? new();
        NumberBox Field(string title, double value, double min, double max) => new()
        { Header = title, Value = value, Minimum = min, Maximum = max, HorizontalAlignment = HorizontalAlignment.Stretch };
        var fee = Field("卖出综合手续费（%）", (double)_quant.Preferences.FeePercent, 0, 99.99);
        var min = Field("最低税后目标空间（%）", (double)options.MinimumReturn, 0, 1000);
        var max = Field("单笔数量上限（发）", options.MaxQuantity, 1, 1_000_000);
        var period = Field("指标采样数", options.Period, 2, 240);
        var tp = Field("净收益止盈（%，0 关闭）", (double)options.TakeProfit, 0, 1000);
        var sl = Field("净收益止损（%，0 关闭）", (double)options.StopLoss, 0, 100);
        var trail = Field("盈利峰值回撤（百分点，0 关闭）", (double)options.TrailingStop, 0, 100);
        var slip = Field("选品与回测双边滑点（%）", (double)options.Slippage, 0, 20);
        var panel = new StackPanel { Spacing = 12 };
        foreach (var field in new[] { fee, min, max, period, tp, sl, trail, slip }) panel.Children.Add(field);
        panel.Children.Add(new TextBlock { Text = "止盈止损按扣除卖出手续费后的收益判断。历史缓存超过 30 分钟不用于持仓提示。", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "量化参数", Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (new[] { fee, min, max, period, tp, sl, trail, slip }.Any(box => !double.IsFinite(box.Value))
                    || max.Value != Math.Truncate(max.Value) || period.Value != Math.Truncate(period.Value))
                    throw new ArgumentException("请填写所有参数，数量和采样数必须为整数。");
                var update = options with { MinimumReturn = Number(min), MaxQuantity = (int)max.Value, Period = (int)period.Value,
                    TakeProfit = Number(tp), StopLoss = Number(sl), TrailingStop = Number(trail), Slippage = Number(slip) };
                _quant.SaveAnalysisSettings(update, Number(fee));
                Fee.Value = fee.Value;
                Reload();
            }
            catch (Exception ex) { args.Cancel = true; dialog.Title = ex.Message; }
        };
        try { await dialog.ShowAsync(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private async void RunBacktest(object sender, RoutedEventArgs e)
    {
        if (_quant is null || _objectId <= 0) return;
        try
        {
            var result = _quant.Backtest(_objectId);
            var options = _quant.Snapshot(DateTimeOffset.Now).Options!;
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
                Text = $"{AmmoLabels.Format(_name, _grade)} · {QuantOptions.Label(options.Strategy)}\n" +
                    $"{result.Begin} — {result.End}\n{result.Samples} 个有效采样\n\n" +
                    $"起始资金 {result.InitialCash:N0} → 结束资金 {result.FinalCash:N0}\n" +
                    $"税后收益率 {result.ReturnPercent:+0.00;-0.00;0}% · {result.Trades.Count} 笔模拟交易\n" +
                    $"胜率 {result.WinRate:0.#}% · 最大回撤 {result.MaxDrawdownPercent:0.00}%\n" +
                    $"盈利因子 {(result.ProfitFactor.HasValue ? result.ProfitFactor.Value.ToString("0.00") : "无亏损样本")}\n" +
                    $"手续费 {_quant.Preferences.FeePercent:0.##}% · 双边滑点 {options.Slippage:0.##}%\n\n" +
                    "信号按已知采样计算，下一采样价格模拟成交；样本末尾结算剩余持仓。未模拟交易量、库存与上架等待。\n\n" +
                    string.Join("\n\n", result.Trades.TakeLast(30).Select(t =>
                        $"{t.BuyTime} 买 {t.BuyPrice:N2} → {t.SellTime} 卖 {t.SellPrice:N2}\n" +
                        $"{t.Quantity:N0} 发 · 净收益 {t.NetProfit:+#,##0.00;-#,##0.00;0} · {t.Reason}")) +
                    (result.Trades.Count > 30 ? "\n\n仅展示最后 30 笔，统计包含全部交易。" : ""),
            };
            await new ContentDialog { XamlRoot = XamlRoot, Title = "历史回测", CloseButtonText = "返回",
                Content = new ScrollViewer { Content = text, MaxHeight = 430 } }.ShowAsync();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
