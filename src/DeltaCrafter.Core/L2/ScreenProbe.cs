using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;

namespace DeltaCrafter.Core.L2;

/// <summary>
/// 画面探针:把「窗口几何 + 截图 + OCR + 点击」组合成流程层可用的原语。
/// 所有读取都基于"捕获当下一帧",不缓存旧帧——过期画面比没有画面更危险。
/// </summary>
public sealed class ScreenProbe
{
    internal const float ProductionButtonMinimumConfidence = .80f;
    private readonly GameWindowBrick _window;
    private readonly ScreenCaptureBrick _capture;
    private readonly PaddleOcrBrick _ocr;
    private readonly InputBrick _input;
    private readonly Func<AnchorTable> _anchors;
    private readonly string _shotsDir;
    private readonly ILogger _log;

    public ScreenProbe(GameWindowBrick window, ScreenCaptureBrick capture, PaddleOcrBrick ocr,
        InputBrick input, Func<AnchorTable> anchors, string shotsDir, ILogger log)
    {
        _window = window;
        _capture = capture;
        _ocr = ocr;
        _input = input;
        _anchors = anchors;
        _shotsDir = shotsDir;
        _log = log.ForContext<ScreenProbe>();
    }

    public AnchorTable Anchors => _anchors();

    public ScreenSpec Screen(string name) => Anchors.Screen(name);

    public CapturedFrame Capture(nint hwnd) => _capture.CaptureClient(_window.ClientRectOnScreen(hwnd));

    public async Task<OcrReadout> ReadCountdownAsync(CapturedFrame frame, NRect roi, double upscale)
    {
        var result = await _ocr.ReadAsync(frame, roi, upscale);
        _log.Debug("生产倒计时(PaddleOCR {Scale}x)：{Readings}", upscale,
            string.Join(" | ", OcrEvidence.Candidates(result).Select(l => $"{l.Text} ({l.Confidence:P0})")));
        return result;
    }

    /// <summary>一次捕获、多区域识别,保证多个读数来自同一帧。
    /// upscale 可指定识别倍率:2x 适合小字号;1x 适合低对比大字(见 CollectFlow 交替倍率观察)。</summary>
    public async Task<OcrReadout[]> ReadSlotsAsync(CapturedFrame frame, IReadOnlyList<NRect> rois,
        IReadOnlyList<IReadOnlyList<string>> expectedNames, double upscale = 2.0)
    {
        if (rois.Count != expectedNames.Count) throw new ArgumentException("Slot target count mismatch");
        var result = new OcrReadout[rois.Count];
        for (int i = 0; i < rois.Count; i++)
            result[i] = await ReadItemAsync(frame, rois[i], expectedNames[i].Concat(Anchors.Keywords.Idle).ToArray(),
                upscale, SlotOcrLayout.IconMasks(rois[i]), slot: true);
        return result;
    }

    /// <summary>单帧多界面判定:返回第一个探针命中的界面名,均未命中返回 null。
    /// 一次截帧多次判定,避免逐界面截图造成的时间错位。</summary>
    public async Task<string?> WhichScreenAsync(nint hwnd, IReadOnlyList<string> screenNames)
    {
        var frame = Capture(hwnd);
        foreach (var name in screenNames)
        {
            var spec = Screen(name);
            if (MatchesScreenTexts(spec, await ReadScreenTextsAsync(frame, spec))) return name;
        }
        return null;
    }

    /// <summary>是否处于指定界面:探针区域 OCR 文本包含约定关键字。</summary>
    public async Task<bool> IsOnAsync(nint hwnd, string screenName)
    {
        var spec = Screen(screenName);
        var texts = await ReadScreenTextsAsync(Capture(hwnd), spec);
        bool on = MatchesScreenTexts(spec, texts);
        _log.Debug("界面判定 {Screen}:{Result}(读到:{Text})", screenName, on, Compact(string.Join(" | ", texts)));
        return on;
    }

    public async Task<IReadOnlyList<string>> ReadScreenTextsAsync(CapturedFrame frame, ScreenSpec spec)
    {
        var probes = new[] { spec.Probe }.Concat(spec.AdditionalProbes);
        var texts = new List<string>();
        foreach (var probe in probes)
        {
            var reading = await _ocr.ReadAsync(frame, probe.Roi, probe.Upscale);
            string text = OcrEvidence.FindTarget(reading, probe.MustContain)?.Text ?? "";
            _log.Debug("界面探针(PaddleOCR)：目标={Target}; 可信匹配={Matched}; 原文={Readings}",
                probe.MustContain, text.Length > 0,
                string.Join(" | ", reading.Lines.Select(l => $"{l.Text} ({l.Confidence:P0})")));
            texts.Add(text);
            if (text.Length == 0) break; // 首探针不成立时无需识别同屏附加探针。
        }
        return texts;
    }

    internal static bool MatchesScreenTexts(ScreenSpec spec, IReadOnlyList<string> texts)
    {
        var probes = new[] { spec.Probe }.Concat(spec.AdditionalProbes).ToArray();
        if (texts.Count != probes.Length) return false;
        for (int i = 0; i < probes.Length; i++)
        {
            string expected = Normalize(probes[i].MustContain);
            if (expected.Length == 0 || !Normalize(texts[i]).Contains(expected, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    public void ClickPoint(nint hwnd, string screenName, string pointName)
    {
        var rect = _window.ClientRectOnScreen(hwnd);
        var p = Screen(screenName).Point(pointName);
        var (x, y) = PixelMapper.ToPixel(p, rect.Left, rect.Top, rect.Width, rect.Height);
        _log.Debug("点击 {Screen}.{Point} → 屏幕({X},{Y})", screenName, pointName, x, y);
        _input.ClickAt(x, y);
    }

    /// <summary>点击帧内像素坐标(OCR 定位到的行中心)。</summary>
    public void ClickFramePoint(nint hwnd, double frameX, double frameY)
    {
        var rect = _window.ClientRectOnScreen(hwnd);
        _input.ClickAt(rect.Left + (int)Math.Round(frameX), rect.Top + (int)Math.Round(frameY));
    }

    /// <summary>将鼠标移离列表,避免把悬停边框误当成 .300 BLK 行的选中状态。</summary>
    public void MovePointerToRoi(nint hwnd, NRect roi)
    {
        var rect = _window.ClientRectOnScreen(hwnd);
        var point = new NPoint { X = roi.X + roi.W / 2, Y = roi.Y + roi.H / 2 };
        var (x, y) = PixelMapper.ToPixel(point, rect.Left, rect.Top, rect.Width, rect.Height);
        _input.MoveTo(x, y);
    }

    /// <summary>在区域中心滚动(负档向下翻列表)。</summary>
    public void ScrollRoi(nint hwnd, NRect roi, int notches)
    {
        var rect = _window.ClientRectOnScreen(hwnd);
        var center = new NPoint { X = roi.X + roi.W / 2, Y = roi.Y + roi.H / 2 };
        var (x, y) = PixelMapper.ToPixel(center, rect.Left, rect.Top, rect.Width, rect.Height);
        _input.ScrollAt(x, y, notches);
    }

    /// <summary>在区域内按完整目标文字找行，并检查该文字框的置信度。
    /// 找不到返回 null,由调用方决定翻页或失败。</summary>
    public async Task<OcrLine?> FindLineAsync(nint hwnd, NRect area, string target)
    {
        var readout = await _ocr.ReadAsync(Capture(hwnd), area);
        return OcrEvidence.FindTarget(readout, target);
    }

    /// <summary>.300 BLK 专用识别保留 OCR 对应原帧,避免文字与品质颜色来自不同画面。</summary>
    public async Task<(CapturedFrame Frame, OcrReadout Readout)> ReadAreaFrameAsync(nint hwnd, NRect area,
        IReadOnlyList<string> expectedNames)
    {
        var frame = Capture(hwnd);
        return (frame, await ReadItemAsync(frame, area, expectedNames,
            iconMasks: ProductionListOcrLayout.IconMasks(area)));
    }

    public async Task<string> ReadFrameRoiAsync(CapturedFrame frame, NRect roi, string expectedName) =>
        (await ReadItemAsync(frame, roi, [expectedName], itemTitle: true)).FullText;

    public async Task<string> ReadProductionButtonAsync(CapturedFrame frame, NRect roi)
    {
        var kw = Anchors.Keywords;
        var targets = kw.ButtonProduce.Concat(kw.ButtonReplenish).Concat(kw.ButtonAbort).ToArray();
        var readout = await ReadItemAsync(frame, roi, targets,
            minimumConfidence: ProductionButtonMinimumConfidence);
        string label = NormalizeProductionButton(readout.FullText);
        if (label != Normalize(readout.FullText))
            _log.Debug("生产操作按钮横线校正：{Raw} → {Label}", readout.FullText, label);
        _log.Debug("生产操作按钮(PaddleOCR)：{Label}", label);
        return label;
    }

    internal static string NormalizeProductionButton(string text)
    {
        string label = Normalize(text);
        // PaddleOCR 会把「一」识别成横线。只修正完整的四字操作按钮，
        // 不放宽物品名、部分按钮或其他错字；调用前仍须通过按钮置信度检查。
        return label.Length == 4 && (label[0] is '—' or '–' or '-' or '－' or '−') &&
            label.AsSpan(1).SequenceEqual("键补齐") ? "一键补齐" : label;
    }

    /// <summary>物品识别使用离线模型，失败即停止，不自动切回识别率较低的路径。</summary>
    private async Task<OcrReadout> ReadItemAsync(CapturedFrame frame, NRect roi,
        IReadOnlyList<string> expectedNames, double upscale = 2.0,
        IReadOnlyList<NRect>? iconMasks = null, bool itemTitle = false, bool slot = false,
        float minimumConfidence = OcrReadout.MinimumItemConfidence)
    {
        var result = await _ocr.ReadAsync(frame, roi, upscale, iconMasks);
        var raw = result;
        _log.Debug("PaddleOCR 物品读数(图标遮罩={Masked})：{Text}",
            iconMasks is { Count: > 0 }, result.FullText.Replace('\n', '|'));
        if (itemTitle)
        {
            var title = ItemTitleOcr.Normalize(result);
            if (title.FullText != result.FullText)
                _log.Debug("详情标题去除数量后缀：{Title}", title.FullText);
            result = title;
        }
        result = OcrMatchFilter.Filter(result, expectedNames, slot,
            line => SlotOcrLayout.IsCountdownText(line, roi, frame.Width, frame.Height))
            with { SourceText = raw.FullText };
        _log.Debug("OCR 目标筛选(匹配度>{Threshold}%)：{Text}; 倒计时未读清={Unreadable}",
            OcrMatchFilter.MinimumMatchPercent, result.FullText.Replace('\n', '|'), result.HasUnreadableCountdown);
        if (result.HasTextBelowConfidence(minimumConfidence))
        {
            string uncertain = string.Join("、", result.Lines.Where(l =>
                !float.IsFinite(l.Confidence) || l.Confidence < minimumConfidence)
                .Select(l => $"{l.Text} ({l.Confidence:P0})"));
            string png = Path.Combine(_shotsDir, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-fail-PaddleOCR.png");
            await _capture.SavePngAsync(frame, png);
            await File.WriteAllTextAsync(Path.ChangeExtension(png, ".txt"),
                $"ROI={roi.X},{roi.Y},{roi.W},{roi.H}; scale={upscale}; iconMasks={iconMasks is { Count: > 0 }}; match>60%; confidence>={minimumConfidence:P0}; targets={string.Join("|", expectedNames)}\n" +
                string.Join("\n", raw.Lines.Select(l =>
                    $"{l.Confidence:F3}\t{l.Text}\t{string.Join("; ", l.Words)}")));
            throw new StepFailedException("识别物品", $"PaddleOCR 识别置信度不足：{uncertain}。已停止本轮，请在画面稳定后重试。诊断截图：{png}", png, result.FullText);
        }
        return result;
    }

    /// <summary>保存整帧截图与全文 OCR 转储(失败现场/校准诊断)。返回(截图路径, OCR 文本)。</summary>
    public async Task<(string PngPath, string OcrText)> DumpAsync(nint hwnd, string tag)
    {
        var frame = Capture(hwnd);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string png = Path.Combine(_shotsDir, $"{stamp}-{tag}.png");
        await _capture.SavePngAsync(frame, png);
        var readout = await _ocr.ReadAsync(frame, upscale: 1.0);
        await File.WriteAllTextAsync(Path.ChangeExtension(png, ".txt"), readout.FullText);
        _log.Information("已保存诊断截图:{Png}", png);
        return (png, readout.FullText);
    }

    /// <summary>OCR 文本归一化:去除空白(中文 OCR 常在词间插入空格)。</summary>
    public static string Normalize(string s) =>
        string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    private static string Compact(string s)
    {
        var one = Normalize(s);
        return one.Length <= 40 ? one : one[..40] + "…";
    }
}
