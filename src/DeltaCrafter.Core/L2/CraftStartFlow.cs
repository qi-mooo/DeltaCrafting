using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>开工结果。Blocked(材料不足/需兑换)是显式业务结果,与步骤失败(异常)分开。</summary>
public sealed record CraftStartResult(bool Started, TimeSpan? Remaining, string? BlockReason);

/// <summary>
/// 「选择配方并开始生产」流程。生产界面右下角为同位置三态按钮,其文字即状态:
/// 「一键补齐」=缺料 →(可选)自动购买;「生产」=可开工;「中止」=已在生产(异常)。
/// 开工成功的判据 = 按钮变为「中止」;随后读「剩余时间」作为唯一调度依据。
/// 配方列表因玩家置顶而顺序各异,按离线 OCR 完整名称与目录唯一匹配定位;滚到列表不再变化为止,并设安全页数上限。
/// </summary>
public sealed class CraftStartFlow
{
    private const int MaxScrollPages = 20; // 安全上限;正常在此之前就会因「见底」停止
    private readonly ScreenProbe _probe;
    private readonly StepRunner _runner;
    private readonly InputBrick _input;
    private readonly ILogger _log;
    private readonly ICatalogLookup _catalog;

    public CraftStartFlow(ScreenProbe probe, StepRunner runner, InputBrick input, ICatalogLookup catalog, ILogger log)
    {
        _probe = probe;
        _runner = runner;
        _input = input;
        _log = log.ForContext<CraftStartFlow>();
        _catalog = catalog;
    }

    /// <param name="searchName">运行期匹配名(通常为 OCR 原文),用于列表定位与选中校验。</param>
    /// <param name="displayName">显示名,用于日志/报告/失败信息。</param>
    public async Task<CraftStartResult> StartAsync(nint hwnd, FacilityKey key, string searchName,
        string displayName, bool autoReplenish, CancellationToken ct)
    {
        string facility = FacilityKeys.DisplayName(key);
        var prodSpec = _probe.Screen(AnchorKeys.Production);
        var kw = _probe.Anchors.Keywords;
        var items = _catalog.ItemsFor(key);
        if (!items.Any(i => CatalogNameResolver.Canonical(i.Name) == CatalogNameResolver.Canonical(displayName)))
            throw new StepFailedException("校验计划物品", "计划物品不在目录中,请先在设置页刷新物品目录并重新选择。");
        searchName = displayName; // 不再使用旧版本保存的任意 OCR 匹配键。

        await _runner.RunAsync(hwnd, new Step(
            $"打开{facility}生产界面",
            () => _probe.ClickPoint(hwnd, AnchorKeys.SpecOpsHome, AnchorKeys.FacilitySlot(key)),
            () => _probe.IsOnAsync(hwnd, AnchorKeys.Production),
            TimeSpan.FromSeconds(12)), ct);

        int? blkGrade = BlkAmmoMatcher.GradeFor(key, displayName, searchName);
        await FindAndSelectItemAsync(hwnd, facility, searchName, displayName, prodSpec, items, blkGrade, ct);

        string label = await ReadActionLabelAsync(hwnd, prodSpec, ct);
        if (LabelHits(label, kw.ButtonAbort))
            throw new StepFailedException($"{facility}开工前检查",
                "生产界面显示「中止」,槽位并非空闲——与总览观察不一致,中止本轮待人工确认。");

        if (LabelHits(label, kw.ButtonReplenish))
        {
            if (!autoReplenish)
            {
                await EscBackToHomeAsync(hwnd, ct);
                return new CraftStartResult(false, null, "材料不足(自动补齐已关闭)");
            }
            await _runner.RunAsync(hwnd, new Step(
                "打开一键补齐清单",
                () => _probe.ClickPoint(hwnd, AnchorKeys.Production, AnchorKeys.PointActionButton),
                () => _probe.IsOnAsync(hwnd, AnchorKeys.ReplenishPopup),
                TimeSpan.FromSeconds(10)), ct);
            _log.Information("{Facility}「{Item}」缺料,自动购买(金额随交易行波动,见游戏账单)。", facility, displayName);
            // 购买是有副作用的动作，只允许点击一次；并行等待成功按钮或仓库已满 Toast，
            // 避免通用步骤重试器在失败时重复购买。
            _probe.ClickPoint(hwnd, AnchorKeys.ReplenishPopup, AnchorKeys.PointBuy);
            label = await WaitForReplenishOutcomeAsync(hwnd, facility, displayName, prodSpec, ct);
            if (!LabelHits(label, kw.ButtonProduce))
            {
                // 兑换类材料交易行买不到:显式受阻,绝不循环烧钱重试。
                await EscBackToHomeAsync(hwnd, ct);
                return new CraftStartResult(false, null, "一键补齐后材料仍不足(可能含需兑换材料)");
            }
        }

        if (!LabelHits(label, kw.ButtonProduce))
        {
            var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-识别操作按钮");
            throw new StepFailedException($"{facility}识别操作按钮",
                $"按钮文字「{label}」无法归类为 生产/一键补齐/中止。诊断截图:{png}", png, dumpText);
        }

        await _runner.RunAsync(hwnd, new Step(
            $"开始生产「{displayName}」",
            () => _probe.ClickPoint(hwnd, AnchorKeys.Production, AnchorKeys.PointActionButton),
            async () => LabelHits(await ReadLabelOnceAsync(hwnd, prodSpec), kw.ButtonAbort),
            TimeSpan.FromSeconds(15)), ct);

        var remaining = await ReadRemainingTimeAsync(hwnd, facility, prodSpec, ct);
        await EscBackToHomeAsync(hwnd, ct);
        _log.Information("{Facility}「{Item}」已开工,剩余 {Remaining}。", facility, displayName, remaining);
        return new CraftStartResult(true, remaining, null);
    }

    private async Task FindAndSelectItemAsync(nint hwnd, string facility, string searchName,
        string displayName, ScreenSpec prodSpec, IReadOnlyList<CatalogItem> items, int? blkGrade, CancellationToken ct)
    {
        var listArea = prodSpec.Roi(AnchorKeys.RoiListArea);
        string expectedName = blkGrade is not null ? BlkAmmoMatcher.GameName : searchName;
        OcrLine? line = null;
        string previousView = "";
        bool reachedBottom = false;
        // 滚动直到找到 / 列表内容不再变化(到底或滚动无效) / 安全上限。每页只读一次。
        for (int page = 0; page < MaxScrollPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var (frame, readout) = await _probe.ReadAreaFrameAsync(hwnd, listArea, [expectedName]);
            var lines = readout.Lines;
            string currentView;
            if (blkGrade is { } grade)
            {
                var candidate = BlkAmmoMatcher.Find(frame, lines, listArea, grade: grade);
                if (candidate is not null)
                    line = new OcrLine(BlkAmmoMatcher.GameName, candidate.X, candidate.Y);
                currentView = BlkAmmoMatcher.ViewSignature(frame, listArea);
                _log.Debug("查找 .300 BLK {Grade}级弹第{Page}页:名称与品质颜色 {Found}",
                    grade, page + 1, candidate is not null);
            }
            else
            {
                var matches = lines.Where(l => CatalogNameResolver.Matches(items, l.Text, searchName)).ToArray();
                line = matches.Length == 1 ? matches[0] : null;
                // 是否到底看整页原文；不能因筛选后都为空或仅剩同一相似项就提前停翻页。
                currentView = ScreenProbe.Normalize(readout.SourceText ?? readout.FullText);
            }
            if (line is not null) break;

            if (currentView.Length > 0 && currentView == previousView)
            {
                reachedBottom = true;
                break;
            }
            previousView = currentView;
            _probe.ScrollRoi(hwnd, listArea, -5);
            await Task.Delay(700, ct);
        }
        if (line is null)
        {
            var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-查找物品");
            throw new StepFailedException($"查找物品「{displayName}」",
                (blkGrade is not null
                    ? $"{facility}未找到能同时确认「.300 BLK」名称与 {blkGrade} 级品质颜色的弹药。"
                    : reachedBottom
                    ? $"{facility}列表已滚到底仍未找到(匹配键「{searchName}」)。请在画面稳定后重试,或核对目录中的完整名称。"
                    : $"{facility}列表滚动 {MaxScrollPages} 页未找到(或滚轮未生效)。") +
                $"诊断截图:{png}", png, dumpText);
        }

        _probe.ClickFramePoint(hwnd, line.CenterX, line.CenterY);
        var titleArea = ItemTitleOcr.AreaFor(displayName, prodSpec.Roi(AnchorKeys.RoiDetailTitle));
        if (blkGrade is not null) _probe.MovePointerToRoi(hwnd, titleArea);
        long deadline = Environment.TickCount64 + 8000;
        // 普通物品保留原有标题强匹配。.300 BLK 五级弹额外确认金色行的白色选中框,
        // 因为同名标题无法区分等级。所有确认必须在补齐材料/生产之前完成。
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(800, ct);
            if (blkGrade is { } grade)
            {
                var (frame, readout) = await _probe.ReadAreaFrameAsync(hwnd, listArea, [expectedName]);
                var selected = BlkAmmoMatcher.Find(frame, readout.Lines, listArea, requireSelected: true, grade: grade);
                string title = await _probe.ReadFrameRoiAsync(frame, titleArea, expectedName);
                if (selected is not null && BlkAmmoIdentity.IsBareName(title)) return;
            }
            else
            {
                string title = await _probe.ReadFrameRoiAsync(_probe.Capture(hwnd), titleArea, expectedName);
                if (CatalogNameResolver.Matches(items, title, searchName)) return;
            }
        }
        var (png2, dump2) = await _probe.DumpAsync(hwnd, "fail-选中物品");
        throw new StepFailedException($"选中物品「{displayName}」",
            (blkGrade is not null ? "点击后未同时确认 .300 BLK 详情标题、品质颜色及该行选中框。"
                : "点击后详情标题未出现该物品。") + $"诊断截图:{png2}", png2, dump2);
    }

    private async Task<string> ReadActionLabelAsync(nint hwnd, ScreenSpec prodSpec, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string label = await ReadLabelOnceAsync(hwnd, prodSpec);
            var kw = _probe.Anchors.Keywords;
            if (LabelHits(label, kw.ButtonProduce) || LabelHits(label, kw.ButtonReplenish) ||
                LabelHits(label, kw.ButtonAbort)) return label;
            await Task.Delay(1200, ct);
        }
        return "";
    }

    /// <summary>
    /// 补齐后的两个明确结果:按钮变为「生产」即成功；仍为「一键补齐」即材料未补齐。
    /// 顶部 Toast 存在时间很短，因此每轮先读仓库容量状态，再读常驻按钮状态。
    /// </summary>
    private async Task<string> WaitForReplenishOutcomeAsync(nint hwnd, string facility,
        string displayName, ScreenSpec prodSpec, CancellationToken ct)
    {
        long started = Environment.TickCount64;
        long deadline = started + 15_000;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.IsOnAsync(hwnd, AnchorKeys.WarehouseFull))
                throw new WarehouseFullException($"{facility}补齐「{displayName}」材料");

            if (await _probe.IsOnAsync(hwnd, AnchorKeys.Production))
            {
                string label = await ReadLabelOnceAsync(hwnd, prodSpec);
                if (LabelHits(label, _probe.Anchors.Keywords.ButtonProduce)) return label;
                // 等足 Toast 的主要显示窗口后，仍为补齐态才判定普通缺料。
                if (Environment.TickCount64 - started >= 3_000 &&
                    LabelHits(label, _probe.Anchors.Keywords.ButtonReplenish))
                    return label;
            }
            await Task.Delay(400, ct);
        }

        var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-确认购买缺料");
        throw new StepFailedException("确认购买缺料",
            $"15s 内未识别到购买结果。诊断截图:{png}", png, dumpText);
    }

    private async Task<string> ReadLabelOnceAsync(nint hwnd, ScreenSpec prodSpec) =>
        await _probe.ReadProductionButtonAsync(_probe.Capture(hwnd), prodSpec.Roi(AnchorKeys.RoiActionButton));

    internal static bool LabelHits(string normalizedLabel, IEnumerable<string> keywords) =>
        keywords.Any(k => k.Length > 0 &&
            normalizedLabel.Equals(ScreenProbe.Normalize(k), StringComparison.Ordinal));

    private async Task<TimeSpan> ReadRemainingTimeAsync(nint hwnd, string facility,
        ScreenSpec prodSpec, CancellationToken ct)
    {
        long deadline = Environment.TickCount64 + 15000;
        var consensus = new CountdownOcr();
        int attempt = 0;
        while (Environment.TickCount64 < deadline)
        {
            ct.ThrowIfCancellationRequested();
            long capturedAt = Environment.TickCount64;
            var reading = await _probe.ReadCountdownAsync(_probe.Capture(hwnd),
                prodSpec.Roi(AnchorKeys.RoiRemainingTime), ++attempt % 2 == 1 ? 1 : 2);
            if (consensus.Observe(reading, capturedAt, Environment.TickCount64, out var remaining)) return remaining;
            await Task.Delay(700, ct);
        }
        var (png, dumpText) = await _probe.DumpAsync(hwnd, "fail-读取剩余时间");
        throw new StepFailedException($"读取{facility}剩余时间",
            $"开工后未读到两次一致且置信度足够的剩余时间,无法调度下一轮。诊断截图:{png}", png, dumpText);
    }

    private Task EscBackToHomeAsync(nint hwnd, CancellationToken ct) =>
        _runner.RunAsync(hwnd, new Step(
            "返回特勤处总览",
            () => _input.PressEscape(),
            () => _probe.IsOnAsync(hwnd, AnchorKeys.SpecOpsHome),
            TimeSpan.FromSeconds(8)), ct);
}
