namespace DeltaCrafter.Core.L0;

/// <summary>通知事件类别。Bark 推送按类别逐项开关;系统 Toast 只显示汇总类事件,避免逐设施刷屏。</summary>
public enum NotificationKind
{
    /// <summary>一轮收取+续造完成(汇总)。</summary>
    RoundCompleted,
    /// <summary>某设施开始制造,附预计完成时间。</summary>
    CraftStarted,
    /// <summary>某设施的制造物品已领取。</summary>
    ItemCollected,
    /// <summary>材料不足、未配置物品等需要人工处理。</summary>
    NeedsManual,
    WarehouseFull,
    RunFailed,
    /// <summary>Steam 上有游戏运行或状态查询失败,任务暂缓。</summary>
    SteamBlocked,
    UpdateAvailable,
}

/// <summary>Bark 推送设置。Url 为 Bark App 里复制的完整地址(服务器 + 设备 key),只存本机设置文件。</summary>
public sealed class BarkSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";

    /// <summary>Bark 内置铃声名(不含 .caf);空串 = 跟随 Bark App 默认铃声。</summary>
    public string Sound { get; set; } = "";
    /// <summary>失败、仓库满、需人工等紧急提醒的铃声。</summary>
    public string UrgentSound { get; set; } = "";

    public bool RoundCompleted { get; set; }
    public bool CraftStarted { get; set; } = true;
    public bool ItemCollected { get; set; }
    public bool NeedsManual { get; set; } = true;
    public bool WarehouseFull { get; set; } = true;
    public bool RunFailed { get; set; } = true;
    public bool SteamBlocked { get; set; } = true;
    public bool UpdateAvailable { get; set; }

    /// <summary>会反复触发的提醒(Steam 暂缓、需人工、失败、仓库满),同一条在窗口期内最多推送的次数。</summary>
    public int RepeatLimit { get; set; } = 1;
    public int RepeatWindowMinutes { get; set; } = 60;

    public static bool IsUrgent(NotificationKind kind) => kind is NotificationKind.RunFailed
        or NotificationKind.WarehouseFull or NotificationKind.NeedsManual;

    public string SoundFor(NotificationKind kind) => IsUrgent(kind) ? UrgentSound : Sound;

    public static bool IsRepeating(NotificationKind kind) => kind is NotificationKind.SteamBlocked
        or NotificationKind.NeedsManual or NotificationKind.RunFailed or NotificationKind.WarehouseFull;

    public bool Allows(NotificationKind kind) => Enabled && kind switch
    {
        NotificationKind.RoundCompleted => RoundCompleted,
        NotificationKind.CraftStarted => CraftStarted,
        NotificationKind.ItemCollected => ItemCollected,
        NotificationKind.NeedsManual => NeedsManual,
        NotificationKind.WarehouseFull => WarehouseFull,
        NotificationKind.RunFailed => RunFailed,
        NotificationKind.SteamBlocked => SteamBlocked,
        NotificationKind.UpdateAvailable => UpdateAvailable,
        _ => false,
    };
}

/// <summary>
/// 重复提醒限流:按「类别 + 标题」(如「工作台需人工处理」)分别计数,滑动窗口内超过次数即丢弃。
/// 非重复类事件(开始制造、领取等)不限流。线程安全。
/// </summary>
public sealed class NotificationThrottle(IClock clock)
{
    private readonly Dictionary<(NotificationKind, string), Queue<DateTimeOffset>> _sent = new();
    private readonly object _gate = new();

    public bool TryAcquire(NotificationKind kind, string title, BarkSettings settings)
    {
        if (!BarkSettings.IsRepeating(kind)) return true;
        int limit = Math.Clamp(settings.RepeatLimit, 1, 100);
        var window = TimeSpan.FromMinutes(Math.Clamp(settings.RepeatWindowMinutes, 1, 1440));
        var now = clock.Now;
        lock (_gate)
        {
            if (!_sent.TryGetValue((kind, title), out var times)) _sent[(kind, title)] = times = new();
            while (times.Count > 0 && now - times.Peek() >= window) times.Dequeue();
            if (times.Count >= limit) return false;
            times.Enqueue(now);
            return true;
        }
    }
}
