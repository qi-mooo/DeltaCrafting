using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaCrafter.App.Services;
using DeltaCrafter.Core.L1;
using Microsoft.UI.Xaml;

namespace DeltaCrafter.App.ViewModels;

/// <summary>
/// USB 首次安装固件:给从未装过本项目固件(或固件已损坏、无法在线更新)的 ESP32-S3 整片写入。
/// 只写固件包列出的映像区域,NVS 中的 Wi-Fi/配对配置保留。写入在后台线程,进度经 IProgress 回 UI。
/// </summary>
public sealed partial class UsbFlashViewModel : ObservableObject
{
    private static readonly (string Label, string? File)[] TargetDefinitions =
    [
        ("T-Display-S3 屏幕", "DeltaCrafter-esp32s3.zip"),
        ("Harp USB 播放器", "DeltaHarp-esp32s3-usb.zip"),
        ("其他固件包(选择 ZIP)…", null),
    ];

    private readonly AppHost _host;

    public IReadOnlyList<string> Targets { get; } = TargetDefinitions.Select(t => t.Label).ToArray();
    public ObservableCollection<EspPortInfo> Ports { get; } = new();

    /// <summary>页面提供的确认对话框(需要 XamlRoot);返回 false 即取消。</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private EspPortInfo? selectedPort;

    [ObservableProperty] private int targetIndex;
    [ObservableProperty] private string status = "连接设备后点「刷新」;全新芯片通常无需按键。";
    [ObservableProperty] private double progressValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RefreshPortsCommand))]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    private bool isBusy;

    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public UsbFlashViewModel(AppHost host)
    {
        _host = host;
        RefreshPorts();
    }

    private bool CanRefresh => !IsBusy;
    private bool CanInstall => !IsBusy && SelectedPort is not null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private void RefreshPorts()
    {
        string? previous = SelectedPort?.PortName;
        Ports.Clear();
        foreach (var port in SerialEspLink.Ports()) Ports.Add(port);
        SelectedPort = Ports.FirstOrDefault(p => p.PortName == previous) ?? Ports.FirstOrDefault();
        Status = Ports.Count == 0
            ? "未发现串口。请用数据线连接设备;若仍无串口,按住 BOOT 再插入 USB。"
            : SelectedPort!.IsEspNative ? "已识别 ESP32-S3 原生 USB,选择固件后安装。" : "选择设备所在串口与固件后安装。";
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedPort is not { } port) return;
        string? path = ResolveBundlePath();
        if (path is null) return;

        EspFlashBundle bundle;
        try { bundle = await Task.Run(() => EspFlashBundle.Load(path)); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            Status = "固件包无效: " + ex.Message;
            return;
        }

        string summary = $"将向 {port.PortName} 写入 {bundle.FirmwareVersion}({bundle.Images.Count} 个映像,{bundle.FlashSize} Flash)。\n\n"
            + "设备现有固件会被覆盖;Wi-Fi 与配对配置保留。写入期间请勿断开 USB。";
        if (ConfirmAsync is not null && !await ConfirmAsync(summary)) return;

        IsBusy = true;
        ProgressValue = 0;
        var progress = new Progress<EspFlashProgress>(p =>
        {
            Status = p.Stage;
            ProgressValue = p.Fraction * 100;
        });
        _host.Log.Information("开始 USB 刷写 {Version} → {Port}", bundle.FirmwareVersion, port.PortName);
        try
        {
            await Task.Run(() =>
            {
                using var link = new SerialEspLink(port.PortName);
                new EspRomFlasher(link).Flash(bundle, progress, _host.AppStopToken);
            });
            Status = $"安装完成:{bundle.FirmwareVersion}。设备已重启;若屏幕未亮,请按一次 RESET。";
            _host.Log.Information("USB 刷写完成 {Version}", bundle.FirmwareVersion);
        }
        catch (OperationCanceledException)
        {
            Status = "已取消。设备固件可能不完整,请重新安装。";
        }
        catch (Exception ex)
        {
            // 串口占用/拔线/ROM 拒绝等都在这里亮给用户;芯片 ROM 不可擦写,失败后总能重新安装。
            Status = "安装失败: " + ex.Message;
            _host.Log.Warning(ex, "USB 刷写失败 {Port}", port.PortName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string? ResolveBundlePath()
    {
        if (TargetDefinitions[TargetIndex].File is { } file)
        {
            string path = Path.Combine(AppLocation.LauncherDirectory, "Firmware", file);
            if (File.Exists(path)) return path;
            Status = $"未找到随包固件 {file},请选择「其他固件包」手动指定。";
            return null;
        }
        nint hwnd = App.MainWindowRef is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;
        return Win32Dialogs.PickFirmwareFile(hwnd);
    }
}
