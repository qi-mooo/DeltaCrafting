using DeltaCrafter.App.Services;
using DeltaCrafter.App.ViewModels;
using DeltaCrafter.Core.L1;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaCrafter.App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; } = AppHost.Current.SettingsVm;

    public SettingsPage()
    {
        InitializeComponent();
        // USB 刷写前的二次确认由页面提供(需要 XamlRoot 弹 ContentDialog)。
        Vm.UsbFlash.ConfirmAsync = ConfirmUsbFlashAsync;
    }

    private async Task<bool> ConfirmUsbFlashAsync(string summary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "通过 USB 安装固件?",
            Content = new TextBlock { Text = summary, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "开始安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>分区标签切换:每个 SelectorBarItem 的 Tag 是对应分区面板的 x:Name,只显示选中的一个。</summary>
    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        string? selected = sender.SelectedItem?.Tag as string;
        // XAML 加载中 IsSelected 也会触发本事件,此时后面的面板字段尚未赋值;初始可见性已由 XAML 给定。
        foreach (var panel in new[] { GamePanel, DataPanel, RunPanel, DevicePanel, AppPanel })
            if (panel is not null) panel.Visibility = panel.Name == selected ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 定位游戏窗口:列出候选窗口让用户点选,把标题+类名固化为匹配规则。
    /// 一次性人工确认胜过猜测窗口类名——不同渠道/版本类名可能不同。
    /// </summary>
    private async void OnPickWindow(object sender, RoutedEventArgs e)
    {
        var candidates = Vm.ListCandidateWindows();
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = candidates.Select(w => $"{w.Title}   ·   {w.ClassName}").ToList(),
            MaxHeight = 320,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择游戏窗口",
            Content = candidates.Count == 0
                ? new TextBlock { Text = "未发现可见窗口。请先启动游戏后重试。", TextWrapping = TextWrapping.Wrap }
                : list,
            PrimaryButtonText = "绑定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary
            && list.SelectedIndex >= 0)
        {
            Vm.ApplyWindowChoice(candidates[list.SelectedIndex]);
        }
    }
}
