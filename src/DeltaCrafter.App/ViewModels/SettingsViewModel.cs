using System.Diagnostics;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeltaCrafter.App.Services;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeltaCrafter.App.ViewModels;

/// <summary>
/// 设置页。所有属性即改即存;开机自启走 schtasks,失败原因原样显示在
/// AutostartError(InfoBar),开关状态回弹到真实值——不假装成功。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ThemeService _theme;
    private bool _autostartEnabled;

    [ObservableProperty] private string steamActivityStatus = "尚未检查";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutostartError))]
    private string autostartError = "";

    [ObservableProperty] private string gamePath;
    [ObservableProperty] private string windowRuleText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckingRingVisibility))]
    private bool isCheckingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateStatus))]
    private string updateStatus = "";

    [ObservableProperty] private InfoBarSeverity updateSeverity = InfoBarSeverity.Informational;

    public bool HasAutostartError => !string.IsNullOrEmpty(AutostartError);
    public bool HasUpdateStatus => !string.IsNullOrEmpty(UpdateStatus);

    /// <summary>检查中才显示按钮内的进度环(项目惯例:VM 直出 Visibility,不引入布尔转换器)。</summary>
    public Visibility CheckingRingVisibility =>
        IsCheckingUpdate ? Visibility.Visible : Visibility.Collapsed;

    public SettingsViewModel(AppHost host, ThemeService theme)
    {
        _host = host;
        _theme = theme;
        gamePath = S.GamePath;
        windowRuleText = DescribeRule();
        try { _autostartEnabled = host.Autostart.IsEnabled(); }
        catch (Exception ex) { AutostartError = ex.Message; }
        host.DeviceApi.Changed += () =>
        {
            OnPropertyChanged(nameof(DeviceApiStatus));
            OnPropertyChanged(nameof(DeviceFirmwareStatus));
        };
        host.ProfitPlan.Changed += () =>
        {
            OnPropertyChanged(nameof(ManufactureDataStatus));
            RefreshManufactureDataCommand.NotifyCanExecuteChanged();
        };
    }

    private AppSettings S => _host.Settings;
    private void Save() => _host.SaveSettings();

    public string ManufactureToken
    {
        get => S.ManufactureApi.Token;
        set
        {
            string token = value?.Trim() ?? "";
            if (token == S.ManufactureApi.Token) return;
            S.ManufactureApi.Token = token;
            Save(); _host.ProfitPlan.SettingsChanged(); OnPropertyChanged();
        }
    }

    public double TechLevel { get => S.ManufactureApi.LevelFor(FacilityKey.TechCenter); set => SetLevel(FacilityKey.TechCenter, value); }
    public double WorkbenchLevel { get => S.ManufactureApi.LevelFor(FacilityKey.Workbench); set => SetLevel(FacilityKey.Workbench, value); }
    public double PharmacyLevel { get => S.ManufactureApi.LevelFor(FacilityKey.PharmacyLab); set => SetLevel(FacilityKey.PharmacyLab, value); }
    public double ArmorLevel { get => S.ManufactureApi.LevelFor(FacilityKey.ArmorStation); set => SetLevel(FacilityKey.ArmorStation, value); }

    private void SetLevel(FacilityKey key, double value)
    {
        if (!double.IsFinite(value)) return;
        int level = Math.Clamp((int)value, 1, 3);
        if (S.ManufactureApi.LevelFor(key) == level) return;
        S.ManufactureApi.FacilityLevels[FacilityKeys.JsonKey(key)] = level;
        Save(); _host.ProfitPlan.SettingsChanged();
    }

    public string ManufactureDataStatus => _host.ProfitPlan.DataRefresh.Detail;
    private bool CanRefreshManufactureData() => !_host.ProfitPlan.DataRefresh.IsRunning;

    [RelayCommand(CanExecute = nameof(CanRefreshManufactureData))]
    private void RefreshManufactureData() => _host.ProfitPlan.TryRefreshData();

    public string DeviceApiStatus => _host.DeviceApi.StatusText;
    public string DeviceFirmwareStatus => _host.DeviceApi.FirmwareStatus;

    [RelayCommand]
    private async Task ImportDeviceFirmwareAsync()
    {
        nint hwnd = App.MainWindowRef is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;
        string? file = Win32Dialogs.PickFirmwareFile(hwnd);
        if (file is not null) await _host.DeviceApi.ImportFirmwareAsync(file);
    }
    public string DeviceApiKey => S.DeviceApi.ApiKey;

    public bool DeviceApiEnabled
    {
        get => S.DeviceApi.Enabled;
        set
        {
            if (S.DeviceApi.Enabled == value) return;
            if (value && string.IsNullOrEmpty(S.DeviceApi.ApiKey)) GenerateDeviceApiKey();
            S.DeviceApi.Enabled = value;
            ApplyDeviceApi();
            OnPropertyChanged();
        }
    }

    public double DeviceApiPort
    {
        get => S.DeviceApi.Port;
        set
        {
            if (!double.IsFinite(value)) return;
            int port = (int)Math.Clamp(value, 1024, 65535);
            if (port == S.DeviceApi.Port) return;
            S.DeviceApi.Port = port;
            ApplyDeviceApi();
            OnPropertyChanged();
        }
    }

    public bool DeviceApiAllowControl
    {
        get => S.DeviceApi.AllowControl;
        set
        {
            if (S.DeviceApi.AllowControl == value) return;
            S.DeviceApi.AllowControl = value;
            ApplyDeviceApi();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void GenerateDeviceApiKey()
    {
        S.DeviceApi.ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ApplyDeviceApi();
        OnPropertyChanged(nameof(DeviceApiKey));
    }

    [RelayCommand]
    private void CopyDeviceApiKey()
    {
        if (string.IsNullOrEmpty(DeviceApiKey)) GenerateDeviceApiKey();
        var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
        data.SetText(DeviceApiKey);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
    }

    private void ApplyDeviceApi()
    {
        Save();
        _host.DeviceApi.Apply();
        OnPropertyChanged(nameof(DeviceApiStatus));
    }

    partial void OnGamePathChanged(string value)
    {
        S.GamePath = value ?? "";
        Save();
    }

    public int LaunchModeIndex
    {
        get => (int)S.LaunchMode;
        set
        {
            if (value is < 0 or > 1 || value == (int)S.LaunchMode) return;
            S.LaunchMode = (GameLaunchMode)value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LauncherSettingsVisibility));
            OnPropertyChanged(nameof(SteamSettingsVisibility));
        }
    }

    public Visibility LauncherSettingsVisibility => S.LaunchMode == GameLaunchMode.Launcher ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SteamSettingsVisibility => S.LaunchMode == GameLaunchMode.Steam ? Visibility.Visible : Visibility.Collapsed;

    public string SteamAppId
    {
        get => S.SteamAppId;
        set { S.SteamAppId = value?.Trim() ?? ""; Save(); OnPropertyChanged(); }
    }

    public string SteamPathDescription => string.IsNullOrWhiteSpace(S.SteamPath) ? "自动检测本机 Steam 客户端" : S.SteamPath;

    public bool SteamActivityEnabled
    {
        get => S.SteamActivity.Enabled;
        set { S.SteamActivity.Enabled = value; Save(); OnPropertyChanged(); }
    }

    public string SteamActivityId
    {
        get => S.SteamActivity.SteamId;
        set { S.SteamActivity.SteamId = value?.Trim() ?? ""; Save(); OnPropertyChanged(); SteamActivityStatus = "尚未检查"; }
    }

    public string SteamActivityApiKey
    {
        get => S.SteamActivity.ApiKey;
        set { S.SteamActivity.ApiKey = value?.Trim() ?? ""; Save(); OnPropertyChanged(); SteamActivityStatus = "尚未检查"; }
    }

    [RelayCommand]
    private async Task CheckSteamActivityAsync()
    {
        string key = SteamActivityApiKey, id = SteamActivityId;
        SteamActivityStatus = "正在查询 Steam…";
        var result = await new SteamActivityClient().CheckAsync(key, id, CancellationToken.None);
        if (key != SteamActivityApiKey || id != SteamActivityId) return;
        SteamActivityStatus = result.Detail;
    }

    [RelayCommand]
    private void BrowseSteamPath()
    {
        nint hwnd = App.MainWindowRef is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;
        var picked = Win32Dialogs.PickExeFile(hwnd);
        if (picked is null) return;
        S.SteamPath = picked;
        Save();
        OnPropertyChanged(nameof(SteamPathDescription));
    }

    [RelayCommand]
    private void ResetSteamPath()
    {
        S.SteamPath = "";
        Save();
        OnPropertyChanged(nameof(SteamPathDescription));
    }

    public double LaunchTimeoutSeconds
    {
        get => S.LaunchTimeoutSeconds;
        set { if (!double.IsNaN(value)) { S.LaunchTimeoutSeconds = Math.Clamp((int)value, 30, 900); Save(); OnPropertyChanged(); } }
    }

    public double LobbyTimeoutSeconds
    {
        get => S.LobbyTimeoutSeconds;
        set { if (!double.IsNaN(value)) { S.LobbyTimeoutSeconds = Math.Clamp((int)value, 30, 900); Save(); OnPropertyChanged(); } }
    }

    public double FailureRetryMinutes
    {
        get => S.FailureRetryMinutes;
        set { if (!double.IsNaN(value)) { S.FailureRetryMinutes = Math.Clamp((int)value, 1, 720); Save(); OnPropertyChanged(); } }
    }

    public int AfterRunIndex
    {
        get => S.AfterRun switch
        {
            AfterRunAction.CloseGame => 0,
            AfterRunAction.KeepRunning => 1,
            _ => 2, // KeepAtLobby
        };
        set
        {
            S.AfterRun = value switch
            {
                0 => AfterRunAction.CloseGame,
                1 => AfterRunAction.KeepRunning,
                _ => AfterRunAction.KeepAtLobby,
            };
            Save();
            OnPropertyChanged();
        }
    }

    public bool PreventSleep
    {
        get => S.PreventSleepWhileWaiting;
        set { S.PreventSleepWhileWaiting = value; Save(); OnPropertyChanged(); }
    }

    public bool CloseToTray
    {
        get => S.CloseToTray;
        set { S.CloseToTray = value; Save(); OnPropertyChanged(); }
    }

    public int ThemeIndex
    {
        get => (int)S.Theme;
        set { S.Theme = (ThemeChoice)value; Save(); _theme.Apply(S.Theme); OnPropertyChanged(); }
    }

    public bool AutostartEnabled
    {
        get => _autostartEnabled;
        set
        {
            if (_autostartEnabled == value) return;
            try
            {
                if (value) _host.Autostart.Enable(Environment.ProcessPath!);
                else _host.Autostart.Disable();
                _autostartEnabled = value;
                AutostartError = "";
            }
            catch (Exception ex)
            {
                AutostartError = ex.Message; // 开关回弹到真实状态
            }
            OnPropertyChanged();
        }
    }

    /// <summary>开发者模式:控制总览页「单步调试」显隐,改动即刻生效。</summary>
    public bool DeveloperMode
    {
        get => S.DeveloperMode;
        set
        {
            if (S.DeveloperMode == value) return;
            S.DeveloperMode = value;
            Save();
            OnPropertyChanged();
            _host.OverviewVm.NotifyDeveloperModeChanged();
        }
    }

    public string VersionText =>
        "v" + (typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>手动检查更新。检查/下载失败把原因显示在 UpdateStatus,不静默吞掉;
    /// 发现新版由 UpdateService 弹窗接管后续下载与安装。CanExecute 防并发点击。</summary>
    [RelayCommand(CanExecute = nameof(CanCheckUpdate))]
    private async Task CheckUpdateAsync()
    {
        IsCheckingUpdate = true;
        CheckUpdateCommand.NotifyCanExecuteChanged();
        UpdateSeverity = InfoBarSeverity.Informational;
        UpdateStatus = "正在检查更新…";
        try
        {
            UpdateStatus = await _host.Updater.CheckFromSettingsAsync();
        }
        catch (Exception ex)
        {
            UpdateSeverity = InfoBarSeverity.Error; // 明确失败:红色错误条,不用蓝色信息条弱化
            UpdateStatus = $"检查失败:{ex.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
            CheckUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheckUpdate() => !IsCheckingUpdate;

    [RelayCommand]
    private void BrowseGamePath()
    {
        nint hwnd = App.MainWindowRef is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;
        var picked = Win32Dialogs.PickExeFile(hwnd);
        if (picked is not null) GamePath = picked; // 属性变更钩子负责落盘
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        Process.Start(new ProcessStartInfo { FileName = _host.Paths.Root, UseShellExecute = true });

    /// <summary>供窗口选择对话框使用:候选窗口(剔除本进程自己)。</summary>
    public IReadOnlyList<GameWindowInfo> ListCandidateWindows()
    {
        int selfPid = Environment.ProcessId;
        return _host.WindowBrick.ListCandidates().Where(w => w.ProcessId != selfPid).ToList();
    }

    public void ApplyWindowChoice(GameWindowInfo chosen)
    {
        S.WindowMatch.ExactTitle = chosen.Title;
        S.WindowMatch.ClassName = chosen.ClassName;
        Save();
        WindowRuleText = DescribeRule();
        _host.Log.Information("窗口匹配规则已绑定:{Title} / {Class}", chosen.Title, chosen.ClassName);
    }

    [RelayCommand]
    private void ResetWindowRule()
    {
        S.WindowMatch.ExactTitle = null;
        S.WindowMatch.ClassName = null;
        Save();
        WindowRuleText = DescribeRule();
    }

    private string DescribeRule() => S.WindowMatch.ExactTitle is { Length: > 0 } t
        ? $"精确标题:{t}"
        : $"标题包含:{S.WindowMatch.TitleContains}";
}
