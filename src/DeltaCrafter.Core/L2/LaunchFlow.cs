using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

public sealed record LaunchOutcome(nint Hwnd, bool LaunchedByUs);

/// <summary>
/// 「确保游戏就绪并到达大厅」流程。通过原有启动器或本机 Steam 启动:
/// 可选启动器(OCR 找「开始游戏」点击)→ 游戏客户端(16:9 大窗,Steam 模式还校验进程路径)→
/// 模式选择(点烽火地带)→ 3D 基地(Tab)→ 大厅。公告和退出菜单重读确认后 ESC;
/// 未知画面连续等待 60 秒后才允许 ESC 兜底(≤3 次),每次重新等待。
/// </summary>
public sealed class LaunchFlow
{
    private static readonly string[] KnownScreens =
        [AnchorKeys.Lobby, AnchorKeys.SpecOpsHome, AnchorKeys.ModeSelectPlay, AnchorKeys.ModeSelect,
            AnchorKeys.Safehouse, AnchorKeys.ModeExitMenu, AnchorKeys.PromoAnnounce];
    private static readonly string[] ModeDestinations =
        [AnchorKeys.Lobby, AnchorKeys.SpecOpsHome, AnchorKeys.Safehouse, AnchorKeys.PromoAnnounce];
    private static readonly string[] ExitMenuDestinations =
        [AnchorKeys.ModeSelectPlay, AnchorKeys.ModeSelect, .. ModeDestinations];
    private static readonly NRect FullFrame = new() { X = 0, Y = 0, W = 1, H = 1 };

    private readonly GameProcessBrick _process;
    private readonly GameWindowBrick _window;
    private readonly ScreenProbe _probe;
    private readonly InputBrick _input;
    private readonly StepRunner _runner;
    private readonly Func<AppSettings> _settings;
    private readonly ILogger _log;
    private readonly AssistantGameSession _assistantGame = new();

    public bool HasAssistantStartedGame() => _assistantGame.IsRunning(GameProcessBrick.ReadIdentity);

    public LaunchFlow(GameProcessBrick process, GameWindowBrick window, ScreenProbe probe,
        InputBrick input, StepRunner runner, Func<AppSettings> settings, ILogger log)
    {
        _process = process;
        _window = window;
        _probe = probe;
        _input = input;
        _runner = runner;
        _settings = settings;
        _log = log.ForContext<LaunchFlow>();
    }

    public async Task<LaunchOutcome> EnsureLobbyAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = _settings();
        var steam = ResolveSteam(s);
        var game = _window.FindGameClient(s.WindowMatch, steam?.InstallDirectory);
        bool launched = false;

        // 「保持运行(最小化)」收尾后客户端处于最小化:客户区几何取不到,16:9 判定
        // 必然失败,若直接走启动器会对着已在运行的游戏白点 240 秒。先还原再找一次。
        if (game is null && _window.TryRestoreMinimizedCandidate(s.WindowMatch, steam?.InstallDirectory))
        {
            await Task.Delay(1200, ct);
            game = _window.FindGameClient(s.WindowMatch, steam?.InstallDirectory);
            if (game is not null) _log.Information("游戏窗口处于最小化,已还原。");
        }

        if (game is null)
        {
            game = await StartGameAsync(s, steam, ct);
            launched = true;
        }

        if (!_window.TryEnsureForeground(game.Hwnd, TimeSpan.FromSeconds(3)))
            throw new StepFailedException("窗口前台化",
                "无法将游戏窗口置于前台。请检查是否有其他置顶/管理员窗口阻挡。");

        await NavigateToLobbyAsync(game.Hwnd, s, ct);
        return new LaunchOutcome(game.Hwnd, launched);
    }

    public GameWindowInfo? FindRunningClient(bool includeMinimized = false)
    {
        var s = _settings();
        return _window.FindGameClient(s.WindowMatch, ResolveSteam(s)?.InstallDirectory, includeMinimized);
    }

    private static SteamGameInstallation? ResolveSteam(AppSettings settings) => settings.LaunchMode switch
    {
        GameLaunchMode.Launcher => null,
        GameLaunchMode.Steam => SteamInstallBrick.Resolve(settings.SteamPath, settings.SteamAppId),
        _ => throw new InvalidOperationException("未知的游戏启动方式,请在设置页重新选择。"),
    };

    /// <summary>允许直接出现客户端,也允许先出现游戏启动器;始终只接受本地游戏进程。</summary>
    private async Task<GameWindowInfo> StartGameAsync(AppSettings s, SteamGameInstallation? steam, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var launcher = _window.FindLauncher(s.WindowMatch.TitleContains, steam?.InstallDirectory);
        DateTime? launchRequestedUtc = null;
        if (launcher is null)
        {
            launchRequestedUtc = DateTime.UtcNow;
            if (steam is not null) _process.LaunchSteam(steam);
            else _process.Launch(s.GamePath);
        }
        else
        {
            _log.Information("发现已打开的启动器:{Title}", launcher.Title);
        }

        var startWords = _probe.Anchors.Keywords.LauncherStart;
        long deadline = Environment.TickCount64 + s.LaunchTimeoutSeconds * 1000L;
        int clicks = 0;
        long lastClickAt = 0;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var game = _window.FindGameClient(s.WindowMatch, steam?.InstallDirectory);
            if (game is not null)
            {
                _log.Information("游戏客户端窗口已出现:{Title}", game.Title);
                if (_assistantGame.Record(GameProcessBrick.ReadIdentity(game.ProcessId), launchRequestedUtc))
                    _log.Information("已记录助手启动的游戏进程 {Pid},后续任务不受该进程的 Steam 游戏状态阻拦。", game.ProcessId);
                _log.Information("等待游戏客户端加载 30 秒,之后按实际画面继续导航。");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return game;
            }

            if (launcher is null || !_window.IsAlive(launcher.Hwnd))
                launcher = _window.FindLauncher(s.WindowMatch.TitleContains, steam?.InstallDirectory);

            // 点击「开始游戏」:最多 2 次(首点 + 30s 后一次显式重试),不盲目连点。
            if (launcher is not null && clicks < 2 && (clicks == 0 || Environment.TickCount64 - lastClickAt > 30_000)
                && _window.TryEnsureForeground(launcher.Hwnd, TimeSpan.FromSeconds(1)))
            {
                foreach (var word in startWords)
                {
                    var line = await _probe.FindLineAsync(launcher.Hwnd, FullFrame, word);
                    if (line is null) continue;
                    launchRequestedUtc ??= DateTime.UtcNow;
                    clicks++;
                    lastClickAt = Environment.TickCount64;
                    if (clicks > 1) _log.Warning("客户端仍未出现,重试点击启动器「{Word}」。", word);
                    else _log.Information("点击启动器「{Word}」。", word);
                    _probe.ClickFramePoint(launcher.Hwnd, line.CenterX, line.CenterY);
                    break;
                }
            }
            await Task.Delay(3000, ct);
        }

        string shot = "";
        if (launcher is not null && _window.IsAlive(launcher.Hwnd))
            try { (shot, _) = await _probe.DumpAsync(launcher.Hwnd, "fail-启动器"); }
            catch (Exception ex) { _log.Error(ex, "保存启动器现场失败。"); }
        throw new StepFailedException("启动游戏客户端",
            $"{s.LaunchTimeoutSeconds}s 内本机游戏客户端窗口未出现。" +
            (steam is not null
                ? "请确认 Steam 已登录、游戏在本机完成更新并运行,并检查窗口绑定;串流窗口不会被接管。"
                : "请确认游戏路径正确,启动器已完成更新和登录,并检查窗口绑定。") +
            (shot.Length > 0 ? $"诊断截图:{shot}" : ""), shot.Length > 0 ? shot : null);
    }

    private async Task NavigateToLobbyAsync(nint hwnd, AppSettings s, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + s.LobbyTimeoutSeconds * 1000L;
        var escapeFallback = new LaunchEscapeFallback();
        int promoEscs = 0;
        long nextLoadingLogAt = 0;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!_window.IsAlive(hwnd))
                throw new StepFailedException("等待大厅", "游戏窗口中途消失(游戏崩溃或被手动关闭)。");

            _window.TryEnsureForeground(hwnd, TimeSpan.FromSeconds(1));
            long observedAt = Environment.TickCount64;
            string? screen = await _probe.WhichScreenAsync(hwnd, KnownScreens);
            if (escapeFallback.Observe(screen, observedAt))
            {
                // 完整 OCR 一轮需要数秒,触发兜底前必须重新截帧,
                // 避免判断的是加载画面,按键时却已经进入模式选择。
                ct.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline) break;
                observedAt = Environment.TickCount64;
                screen = await _probe.WhichScreenAsync(hwnd, KnownScreens);
                bool stillUnknown = escapeFallback.Observe(screen, observedAt);
                ct.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline) break;
                if (stillUnknown && _window.IsAlive(hwnd)
                    && _window.TryEnsureForeground(hwnd, TimeSpan.FromSeconds(1)))
                {
                    _input.PressEscape();
                    escapeFallback.RecordEscape(Environment.TickCount64);
                    _log.Information("画面连续 60 秒未识别,重新检查仍未就绪,按 ESC 兜底({N}/3);再等待 60 秒。",
                        escapeFallback.Attempts);
                    await Task.Delay(3000, ct);
                    continue;
                }
            }
            if (screen is not null) nextLoadingLogAt = 0;

            // 识别期间页面可能还在切换。明确可关闭的页面也要间隔两秒
            // 重新截帧确认,避免根据已经消失的公告或菜单发送 ESC。
            if (screen is AnchorKeys.ModeExitMenu or AnchorKeys.PromoAnnounce)
            {
                await Task.Delay(2000, ct);
                if (Environment.TickCount64 >= deadline) break;
                if (!_window.IsAlive(hwnd))
                    throw new StepFailedException("等待大厅", "游戏窗口中途消失(游戏崩溃或被手动关闭)。");
                if (!_window.TryEnsureForeground(hwnd, TimeSpan.FromSeconds(1))
                    || !await _probe.IsOnAsync(hwnd, screen))
                {
                    _log.Information("公告或菜单画面已变化,继续等待页面就绪。");
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                if (Environment.TickCount64 >= deadline) break;
            }
            switch (screen)
            {
                case AnchorKeys.Lobby:
                case AnchorKeys.SpecOpsHome:
                    _log.Information("已到达{Screen}。", screen == AnchorKeys.Lobby ? "大厅" : "特勤处");
                    return;
                case AnchorKeys.ModeSelect:
                    _log.Information("模式选择界面,点击「烽火地带」。");
                    _probe.ClickPoint(hwnd, AnchorKeys.ModeSelect, AnchorKeys.PointModeEntry);
                    await Task.Delay(2500, ct);
                    break;
                case AnchorKeys.ModeSelectPlay:
                    _log.Information("新版模式选择界面,点击第一张「烽火地带」卡片的「前往游玩」。");
                    await _runner.RunAsync(hwnd, new Step(
                        "前往游玩-烽火地带",
                        () => _probe.ClickPoint(hwnd, AnchorKeys.ModeSelectPlay, AnchorKeys.PointModeEntry),
                        async () => await _probe.WhichScreenAsync(hwnd, ModeDestinations) is not null,
                        TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64)),
                        RetryOnce: false), ct);
                    break;
                case AnchorKeys.ModeExitMenu:
                    _log.Information("检测到退出游戏菜单,按 ESC 返回模式选择。");
                    await _runner.RunAsync(hwnd, new Step(
                        "返回模式选择",
                        () => _input.PressEscape(),
                        async () => await _probe.WhichScreenAsync(hwnd, ExitMenuDestinations) is not null,
                        TimeSpan.FromMilliseconds(Math.Min(10_000, Math.Max(1, deadline - Environment.TickCount64))),
                        RetryOnce: false), ct);
                    break;
                case AnchorKeys.Safehouse:
                    _log.Information("特勤基地界面,按 Tab 进入大厅。");
                    _input.PressTab();
                    await Task.Delay(2500, ct);
                    break;
                case AnchorKeys.PromoAnnounce:
                    // 公告可多页:每页都重新确认,8 页关不掉则留现场明确失败。
                    if (++promoEscs > 8)
                    {
                        var (promoPng, promoDump) = await _probe.DumpAsync(hwnd, "fail-活动公告");
                        throw new StepFailedException("跳过活动公告",
                            $"按 ESC 8 次后活动公告仍未关闭。诊断截图:{promoPng}", promoPng, promoDump);
                    }
                    _log.Information("活动公告页,按 ESC 跳过({N}/8)。", promoEscs);
                    _input.PressEscape();
                    await Task.Delay(1500, ct);
                    break;
                default:
                    if (Environment.TickCount64 >= nextLoadingLogAt)
                    {
                        _log.Information("等待登录/加载完成,尚未识别到可操作页面,继续等待。");
                        nextLoadingLogAt = Environment.TickCount64 + 30_000;
                    }
                    await Task.Delay(3000, ct);
                    break;
            }
        }

        var (png, dump) = await _probe.DumpAsync(hwnd, "fail-等待大厅");
        throw new StepFailedException("等待大厅",
            $"{s.LobbyTimeoutSeconds}s 内未到达大厅(登录卡住或锚点需微调)。诊断截图:{png}", png, dump);
    }
}
