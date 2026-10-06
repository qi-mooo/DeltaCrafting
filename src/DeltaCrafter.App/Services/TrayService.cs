using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace DeltaCrafter.App.Services;

/// <summary>
/// 托盘图标与菜单。应用的常驻形态:窗口关闭只是隐藏,调度循环继续;
/// 真正退出仅经托盘「退出」。SecondWindow 菜单模式是 unpackaged WinUI 下的可靠选择。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly DispatcherQueueTimer _retryTimer;
    private readonly MainWindow _window;
    private readonly ILogger _log;
    private int _attempts;
    private bool _disposed;

    public bool IsAvailable => !_disposed && _icon.TrayIcon.IsCreated;

    public TrayService(MainWindow window, Action runNow, Action exit, ILogger log)
    {
        _window = window;
        _log = log.ForContext<TrayService>();
        var flyout = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开面板" };
        open.Click += (_, _) => window.RestoreFromTray();
        var run = new MenuFlyoutItem { Text = "立即开始制造" };
        run.Click += (_, _) => runNow();
        var quit = new MenuFlyoutItem { Text = "退出" };
        quit.Click += (_, _) => exit();
        flyout.Items.Add(open);
        flyout.Items.Add(run);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(quit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "三角洲特勤助手",
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            ContextMenuMode = ContextMenuMode.SecondWindow,
            ContextFlyout = flyout,
            LeftClickCommand = new RelayCommand(window.RestoreFromTray),
            NoLeftClickDelay = true,
        };
        _retryTimer = window.DispatcherQueue.CreateTimer();
        _retryTimer.Interval = TimeSpan.FromSeconds(2);
        _retryTimer.Tick += OnRetry;
        TryCreate();
    }

    private void OnRetry(DispatcherQueueTimer sender, object args) => TryCreate();

    private void TryCreate()
    {
        if (_disposed) return;
        _attempts++;
        try
        {
            _icon.ForceCreate();
            _retryTimer.Stop();
            _log.Information("托盘图标已就绪(第 {Attempt} 次尝试)。", _attempts);
        }
        catch (InvalidOperationException ex) when (!_icon.TrayIcon.IsCreated)
        {
            // 登录时 Explorer 可能尚未接受 Shell_NotifyIcon；在 UI 线程重试同一图标。
            if (_attempts == 1 || _attempts == 15)
                _log.Warning(ex, "通知区尚未就绪，稍后重试托盘图标(第 {Attempt} 次)。", _attempts);
            if (_attempts == 15)
            {
                _retryTimer.Interval = TimeSpan.FromSeconds(10);
                _window.RestoreFromTray();
            }
            _retryTimer.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retryTimer.Stop();
        _retryTimer.Tick -= OnRetry;
        // SecondWindow 会先关闭承载右键菜单的隐藏窗口再删除托盘图标；退出正处于该窗口
        // 的点击回调中，因此先同步隐藏图标，避免窗口收尾期间通知区仍显示残影。
        _icon.Visibility = Visibility.Collapsed;
        _icon.Dispose();
    }
}
