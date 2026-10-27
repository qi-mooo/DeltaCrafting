using DeltaCrafter.App.Pages;
using DeltaCrafter.App.Services;
using DeltaCrafter.App.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeltaCrafter.App;

/// <summary>
/// 应用壳:Mica 背景 + 自定义标题栏 + 左侧导航。关闭按钮默认最小化到托盘
/// (CloseToTray 设置),真正退出只经 RequestExit(托盘菜单)。
/// </summary>
public sealed partial class MainWindow : Window
{
    public ShellViewModel Shell { get; }
    public string WindowTitle { get; } =
        $"三角洲特勤助手v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";
    private bool _exitRequested;
    private bool _learningDetailsOpen;
    private readonly DispatcherQueueTimer _learningTicker;

    public MainWindow()
    {
        Shell = AppHost.Current.ShellVm;
        InitializeComponent();
        _learningTicker = DispatcherQueue.CreateTimer();
        _learningTicker.Interval = TimeSpan.FromSeconds(1);
        _learningTicker.Tick += (_, _) => RefreshOcrLearning();
        Closed += (_, _) => _learningTicker.Stop();
        Nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateLearningVisibility());
        UpdateLearningVisibility();

        Title = WindowTitle;
        // Mica 云母:Win11 生效;更旧系统框架自动回退纯色背景(仅视觉降级,已记日志)。
        SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1160, 720));
        AppWindow.Closing += OnClosing;

        if (Content is FrameworkElement root)
            root.ActualThemeChanged += (_, _) => TitleBarHelper.ApplyButtonColors(this);
        TitleBarHelper.ApplyButtonColors(this);

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    public void RestoreFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
        Activate();
    }

    public void RequestExit()
    {
        _exitRequested = true;
        AppHost.Current.Shutdown();
        Application.Current.Exit();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (!_exitRequested && AppHost.Current.Settings.CloseToTray)
        {
            e.Cancel = true;
            if (AppHost.Current.IsTrayAvailable) sender.Hide();
            else RestoreFromTray();
            return;
        }
        AppHost.Current.Shutdown();
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs e)
    {
        // 设置页走自定义底部项(内置 Settings 项已禁用,其文案会跟随系统语言变英文)。
        Type? page = ((e.SelectedItem as NavigationViewItem)?.Tag as string) switch
        {
            "overview" => typeof(OverviewPage),
            "plan" => typeof(PlanPage),
            "logs" => typeof(LogPage),
            "tools" => typeof(ToolsPage),
            "settings" => typeof(SettingsPage),
            _ => null,
        };
        if (page is not null && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    private void UpdateLearningVisibility()
    {
        LearningNavItem.Visibility = Nav.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        if (Nav.IsPaneOpen)
        {
            RefreshOcrLearning();
            _learningTicker.Start();
        }
        else _learningTicker.Stop();
    }

    private void RefreshOcrLearning()
    {
        var status = AppHost.Current.Probe.OcrLearningStatus;
        LearningSummary.Text = status.TotalSamples == 0
            ? "OCR 学习"
            : $"OCR 学习 {status.ConfirmedSamples}/{status.TotalSamples}";
        ToolTipService.SetToolTip(LearningNavItem, status.TotalSamples == 0
            ? "尚未学习 · 点击查看详情"
            : $"已确认 {status.ConfirmedSamples}/{status.TotalSamples}，可复用 {status.ReusableSamples} 个区域 · 点击查看详情");
        LearningProgress.Value = status.TotalSamples == 0
            ? 0 : (double)status.ConfirmedSamples / status.TotalSamples;
        LearningProgress.Visibility = status.TotalSamples == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer != LearningNavItem || _learningDetailsOpen) return;
        _learningDetailsOpen = true;
        try { await ShowOcrLearningDetailsAsync(); }
        finally { _learningDetailsOpen = false; }
    }

    private async Task ShowOcrLearningDetailsAsync()
    {
        var status = AppHost.Current.Probe.OcrLearningStatus;
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
            XamlRoot = Content.XamlRoot,
            Title = $"OCR 学习详情 ({status.ConfirmedSamples}/{status.TotalSamples})",
            Content = new ScrollViewer { Content = rows, MaxHeight = 420 },
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }
}
