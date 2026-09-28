namespace DeltaCrafter.Core.L0;

/// <summary>局域网设备接口。默认关闭,所有请求均需配对密钥,远程控制单独开启。</summary>
public sealed class DeviceApiSettings
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 17890;
    public string ApiKey { get; set; } = "";
    public bool AllowControl { get; set; }

    public void Validate()
    {
        if (Port is < 1024 or > 65535)
            throw new ArgumentException("设备 API 端口须为 1024–65535。");
        if (ApiKey.Length is < 32 or > 128 || !ApiKey.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("设备 API 密钥须为 32–128 位英文字母或数字,请重新生成密钥。");
    }
}

public sealed record DeviceFacilityStatus(
    string Key, string Name, bool Enabled, string CraftMode, string PlannedItemName,
    string Phase, string ItemName, DateTimeOffset? ReadyAt, long? RemainingSeconds,
    string? ManualReason, DateTimeOffset? ObservedAt, long? TotalSeconds = null);

public sealed record DeviceStatus(
    int ApiVersion, string AppVersion, DateTimeOffset ServerTime,
    string Mode, string Detail, bool IsRunning, bool AutoLoopEnabled, bool ControlEnabled,
    DateTimeOffset? NextRunAt, long? NextRunInSeconds,
    DateTimeOffset? LastRunAt, string? LastRunSummary, bool LastRunFailed,
    IReadOnlyList<DeviceFacilityStatus> Facilities,
    DeviceGameStatus? Game = null, bool SteamDetectionEnabled = false,
    string AfterRun = "CloseGame", bool SettingsSupported = true, bool SyncSupported = true);

public sealed record DeviceGameStatus(string State, string Detail, DateTimeOffset? CheckedAt);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceSettingsRequest(string? Facility = null, bool? Enabled = null,
    string? CraftMode = null, bool? AutoLoopEnabled = null, bool? SteamDetectionEnabled = null,
    string? AfterRun = null)
{
    public bool IsValid()
    {
        int global = (AutoLoopEnabled.HasValue ? 1 : 0) + (SteamDetectionEnabled.HasValue ? 1 : 0)
            + (AfterRun is not null ? 1 : 0);
        if (Facility is not null)
            return global == 0 && FacilityKeys.All.Any(k => FacilityKeys.JsonKey(k) == Facility)
                && (Enabled.HasValue ^ (CraftMode is not null))
                && (CraftMode is null or "Custom" or "HourlyProfit" or "TotalProfit");
        return global == 1 && !Enabled.HasValue && CraftMode is null
            && (AfterRun is null or "CloseGame" or "KeepRunning" or "KeepAtLobby");
    }
}

public sealed record DeviceActionRequest(string Action);
public sealed record DeviceActionResult(int StatusCode, string Message);
