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
    string? ManualReason, DateTimeOffset? ObservedAt);

public sealed record DeviceStatus(
    int ApiVersion, string AppVersion, DateTimeOffset ServerTime,
    string Mode, string Detail, bool IsRunning, bool AutoLoopEnabled, bool ControlEnabled,
    DateTimeOffset? NextRunAt, long? NextRunInSeconds,
    DateTimeOffset? LastRunAt, string? LastRunSummary, bool LastRunFailed,
    IReadOnlyList<DeviceFacilityStatus> Facilities);

public sealed record DeviceActionRequest(string Action);
public sealed record DeviceActionResult(int StatusCode, string Message);
