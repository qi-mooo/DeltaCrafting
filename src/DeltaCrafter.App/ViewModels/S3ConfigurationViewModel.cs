using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaCrafter.App.Services;
using DeltaCrafter.Core.L1;
using Microsoft.UI.Xaml;

namespace DeltaCrafter.App.ViewModels;

public sealed partial class S3ConfigurationViewModel : ObservableObject
{
    private readonly AppHost _host;
    private string _topology = "";
    private DateTimeOffset _lastScan;
    public ObservableCollection<S3UsbDevice> Devices { get; } = new();
    public ObservableCollection<S3ApiAddress> Addresses { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ConnectCommand))]
    private bool isBusy;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(WifiVisibility), nameof(ConnectionDescription))]
    private S3UsbDevice? selectedDevice;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private S3ApiAddress? selectedAddress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiVisibility), nameof(WifiPasswordVisibility))]
    private bool changeWifi;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WifiPasswordVisibility))]
    private bool openWifi;
    [ObservableProperty] private string wifiSsid = "";
    [ObservableProperty] private string _wifiPassword = "";
    [ObservableProperty] private string status = "用数据线连接 S3,进入此页后自动识别。设备需运行四宫格程序。";

    public S3ConfigurationViewModel(AppHost host) => _host = host;
    private bool CanScan => !IsBusy && !_host.SettingsVm.UsbFlash.IsBusy;
    private bool CanConnect => CanScan && SelectedDevice is not null && SelectedAddress is not null;
    private bool NeedsWifi => ChangeWifi || SelectedDevice is { Info.ApiConfigureSupported: false }
        || SelectedDevice is { Info.WifiSsid.Length: 0 };
    public Visibility WifiVisibility => SelectedDevice is not null && NeedsWifi ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WifiPasswordVisibility => WifiVisibility == Visibility.Visible && !OpenWifi
        ? Visibility.Visible : Visibility.Collapsed;
    public string ConnectionDescription => SelectedDevice is not { } device ? "选择识别到的 S3 屏幕。"
        : !device.Info.ApiConfigureSupported ? "旧固件需要填写 Wi-Fi;先升级 S3 至 v32 后,改地址可保留原 Wi-Fi。"
        : $"当前接口: {(device.Info.BaseUrl.Length > 0 ? device.Info.BaseUrl : "未配置")} · {(device.Info.ApiOnline ? "已连接" : "未连接")}";

    partial void OnChangeWifiChanged(bool value)
    {
        OnPropertyChanged(nameof(WifiVisibility));
        OnPropertyChanged(nameof(WifiPasswordVisibility));
    }

    partial void OnOpenWifiChanged(bool value) => OnPropertyChanged(nameof(WifiPasswordVisibility));

    partial void OnSelectedDeviceChanged(S3UsbDevice? value)
    {
        if (!ChangeWifi && value?.Info.WifiSsid.Length > 0) WifiSsid = value.Info.WifiSsid;
        SelectedAddress = S3ConfigurationClient.ChooseAddress(Addresses.ToArray(), value?.Info.Ip);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private Task ScanAsync() => RefreshAsync(true);

    public void UsbActivityChanged()
    {
        if (!_host.SettingsVm.UsbFlash.IsBusy) _topology = "";
        ScanCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    public async Task RefreshAsync(bool force)
    {
        if (!CanScan) return;
        IsBusy = true;
        try
        {
            var (ports, addresses) = await Task.Run(() => (SerialEspLink.Ports(),
                S3ConfigurationClient.LocalAddresses(_host.Settings.DeviceApi.Port)), _host.AppStopToken);
            string topology = string.Join("|", ports.Where(p => p.IsEspNative).Select(p => p.PortName))
                + ";" + string.Join("|", addresses.Select(a => $"{a.Url}/{a.PrefixLength}/{a.HasGateway}"));
            bool retry = Devices.Count == 0 && ports.Any(p => p.IsEspNative)
                && DateTimeOffset.UtcNow - _lastScan > TimeSpan.FromSeconds(12);
            if (!force && topology == _topology && !retry) return;
            _topology = topology;
            _lastScan = DateTimeOffset.UtcNow;
            string? previousPort = SelectedDevice?.Port.PortName;
            string? previousAddress = SelectedAddress?.Url;
            Status = "正在识别 S3 和电脑局域网地址…";
            var devices = await S3ConfigurationClient.ScanAsync(ports, _host.AppStopToken);
            Addresses.Clear(); foreach (var address in addresses) Addresses.Add(address);
            Devices.Clear(); foreach (var device in devices) Devices.Add(device);
            SelectedDevice = devices.FirstOrDefault(d => d.Port.PortName == previousPort)
                ?? (devices.Count == 1 ? devices[0] : null);
            SelectedAddress = addresses.FirstOrDefault(a => a.Url == previousAddress)
                ?? S3ConfigurationClient.ChooseAddress(addresses, SelectedDevice?.Info.Ip);
            Status = devices.Count == 0
                ? "未识别到 S3 屏幕。请检查数据线、关闭串口工具;声音控制或下载模式下请按 RESET 返回四宫格,再点识别。"
                : addresses.Count == 0 ? "已识别 S3,但电脑没有可用的局域网 IPv4 地址。"
                : devices.Count > 1 && SelectedDevice is null ? $"已发现 {devices.Count} 台 S3,请选择要配置的设备。"
                : SelectedAddress is null ? "已识别 S3,存在多个网络接口,请选择与 S3 互通的地址。"
                : "已自动识别。点击连接 / 更新地址,自动写入电脑地址与配对密钥。";
        }
        catch (OperationCanceledException) { Status = "识别已取消。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Net.NetworkInformation.NetworkInformationException)
        { _topology = ""; Status = "识别失败,请检查 USB 连接和电脑网络后重试。"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (SelectedDevice is not { } device || SelectedAddress is not { } address) return;
        IsBusy = true;
        try
        {
            string? ssid = NeedsWifi ? WifiSsid : null;
            if (NeedsWifi && !OpenWifi && string.IsNullOrEmpty(WifiPassword))
                throw new ArgumentException("为保护设备凭据,请输入 Wi-Fi 密码;无密码网络请勾选开放 Wi-Fi。");
            string? password = NeedsWifi ? OpenWifi ? "" : WifiPassword : null;
            _ = S3ConfigurationClient.Configuration(device.Info, address.Url, new string('A', 64), ssid, password);
            string key = _host.Settings.DeviceApi.ApiKey;
            _ = S3ConfigurationClient.Configuration(device.Info, address.Url, key, ssid, password);
            // 配对按钮授权启用本机服务;沿用现有密钥,不要求用户复制凭据。
            _host.SettingsVm.DeviceApiEnabled = true;
            Status = "正在保存连接配置,等待 S3 重启并验证连接…";
            var info = await S3ConfigurationClient.ConfigureAsync(device, address.Url,
                key, ssid, password, _host.AppStopToken);
            WifiPassword = "";
            OpenWifi = false;
            if (info is not null)
            {
                int index = Devices.IndexOf(device);
                var updated = new S3UsbDevice(device.Port, info);
                if (index >= 0) Devices[index] = updated;
                SelectedDevice = updated;
                SelectedAddress = address;
            }
            Status = info?.ApiOnline == true ? $"已连接: {device.Port.PortName} → {address.Url}"
                : "配置已保存,尚未确认连接。请确认 Wi-Fi 与电脑互通,并允许设备 API 端口通过专用网络防火墙。";
        }
        catch (OperationCanceledException) { Status = "操作已取消,请重新识别确认设备状态。"; }
        catch (ArgumentException ex) { Status = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        { Status = ex is IOException ? ex.Message : "串口连接失败,请关闭其他串口工具并重新识别。"; }
        finally { IsBusy = false; }
    }
}
