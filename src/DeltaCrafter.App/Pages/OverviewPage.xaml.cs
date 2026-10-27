using DeltaCrafter.App.Services;
using DeltaCrafter.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaCrafter.App.Pages;

public sealed partial class OverviewPage : Page
{
    public OverviewViewModel Vm { get; } = AppHost.Current.OverviewVm;

    public OverviewPage()
    {
        InitializeComponent();
        // 取消制造的二次确认由页面提供(需要 XamlRoot 弹 ContentDialog)。
        Vm.ConfirmCancelAsync = ConfirmCancelAsync;
        Vm.ShowOcrLearningDetailsAsync = ShowOcrLearningDetailsAsync;
    }

    private async Task<bool> ConfirmCancelAsync(FacilityCardModel model)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"取消{model.Name}的制造?",
            Content = $"将中止「{model.ItemName}」的制造。游戏通常不返还已消耗的材料,此操作不可撤销。",
            PrimaryButtonText = "中止制造",
            CloseButtonText = "返回",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowOcrLearningDetailsAsync()
    {
        var status = Vm.OcrLearning;
        var rows = new StackPanel { Spacing = 8 };
        if (status.Entries.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text = "还没有可信 OCR 样本。完成一次稳定识别后会开始学习，第二次确认后才启用截图快速复用。",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var entry in status.Entries)
            {
                string state = entry.Reusable ? "可快速复用" : entry.Observations < 2 ?
                    $"学习中 ({entry.Observations}/2)" : $"待复核 (失败 {entry.ReuseFailures}/2)";
                rows.Children.Add(new TextBlock
                {
                    Text = $"{entry.Text}  ·  {state}\n区域 {entry.X},{entry.Y} {entry.Width}×{entry.Height}  ·  {entry.VerifiedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"OCR 学习详情 ({status.ConfirmedSamples}/{status.TotalSamples})",
            Content = new ScrollViewer { Content = rows, MaxHeight = 420 },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
