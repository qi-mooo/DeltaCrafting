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
    string? ManualReason, DateTimeOffset? ObservedAt, long? TotalSeconds = null,
    DeviceDataRefreshStatus? ProfitRefresh = null);

public sealed record DeviceStatus(
    int ApiVersion, string AppVersion, DateTimeOffset ServerTime,
    string Mode, string Detail, bool IsRunning, bool AutoLoopEnabled, bool ControlEnabled,
    DateTimeOffset? NextRunAt, long? NextRunInSeconds,
    DateTimeOffset? LastRunAt, string? LastRunSummary, bool LastRunFailed,
    IReadOnlyList<DeviceFacilityStatus> Facilities,
    DeviceGameStatus? Game = null, bool SteamDetectionEnabled = false,
    string AfterRun = "CloseGame", bool SettingsSupported = true, bool SyncSupported = true,
    bool ItemSelectionSupported = false, bool CloseGameSupported = false,
    bool DataRefreshSupported = false, DeviceDataRefreshStatus? DataRefresh = null,
    bool ProfitRefreshSupported = false);

public sealed record DeviceDataRefreshStatus(bool IsRunning, string Detail, DateTimeOffset? CompletedAt = null);

public sealed record DeviceItemList(string Facility, string SelectedItemName, IReadOnlyList<string> Items)
{
    public static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name)
        && System.Text.Encoding.UTF8.GetByteCount(name) <= 192 && !name.Any(char.IsControl);

    public static DeviceItemList Create(FacilityPlan plan, IEnumerable<string> catalog) => new(
        FacilityKeys.JsonKey(plan.Key), plan.ItemName,
        catalog.Append(plan.ItemName).Where(ValidName).Distinct(StringComparer.Ordinal).ToArray());

    public static DeviceActionResult? ValidateSelection(FacilityPlan plan, IEnumerable<string> catalog, string name)
    {
        if (plan.Mode != CraftMode.Custom) return new(409, "custom_mode_required");
        return ValidName(name) && Create(plan, catalog).Items.Contains(name, StringComparer.Ordinal)
            ? null : new(400, "unknown_item");
    }
}

public sealed record DeviceGameStatus(string State, string Detail, DateTimeOffset? CheckedAt);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceSettingsRequest(string? Facility = null, bool? Enabled = null,
    string? CraftMode = null, bool? AutoLoopEnabled = null, bool? SteamDetectionEnabled = null,
    string? AfterRun = null, string? PlannedItemName = null)
{
    public bool IsValid()
    {
        int global = (AutoLoopEnabled.HasValue ? 1 : 0) + (SteamDetectionEnabled.HasValue ? 1 : 0)
            + (AfterRun is not null ? 1 : 0);
        if (Facility is not null)
            return global == 0 && FacilityKeys.All.Any(k => FacilityKeys.JsonKey(k) == Facility)
                && ((Enabled.HasValue ? 1 : 0) + (CraftMode is not null ? 1 : 0)
                    + (PlannedItemName is not null ? 1 : 0) == 1)
                && (PlannedItemName is null || DeviceItemList.ValidName(PlannedItemName))
                && (CraftMode is null or "Custom" or "HourlyProfit" or "TotalProfit");
        return global == 1 && !Enabled.HasValue && CraftMode is null && PlannedItemName is null
            && (AfterRun is null or "CloseGame" or "KeepRunning" or "KeepAtLobby");
    }
}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record DeviceActionRequest(string Action, string? Facility = null)
{
    public bool IsValid() => Action == "refresh-profit"
        ? FacilityKeys.All.Any(k => FacilityKeys.JsonKey(k) == Facility)
        : Facility is null && Action is ("start" or "sync" or "close-game" or "refresh-data" or "stop" or "pause" or "resume");
}
public sealed record DeviceActionResult(int StatusCode, string Message);
