using DeltaCrafter.Core.L0;
using Serilog;

namespace DeltaCrafter.Core.L1;

/// <summary>区域图像比对与自学习区域 OCR 竞速，首个可信结果获胜。每次都使用调用方的新帧。</summary>
internal sealed class LearnedOcrReader
{
    private readonly Func<CapturedFrame, NRect, double, CancellationToken, Task<OcrReadout>> _ocr;
    private readonly OcrVisualMemory _memory;
    private readonly ILogger? _log;

    internal LearnedOcrReader(Func<CapturedFrame, NRect, double, CancellationToken, Task<OcrReadout>> ocr,
        string? path = null, ILogger? log = null)
    {
        _ocr = ocr;
        _memory = new(path, log);
        _log = log;
    }

    internal async Task<OcrReadout> ReadAsync(CapturedFrame frame, NRect area, double upscale,
        string context, string? target = null, float minimumConfidence = OcrReadout.MinimumItemConfidence,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string key = OcrVisualMemory.Key(frame, area, upscale, context, target, minimumConfidence);
        var candidates = _memory.Candidates(key);
        var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // 两路独立排入线程池，图像比对不等待 Paddle 的模型锁/预热/推理。
        var visual = Task.Run(() => Compare(frame, candidates, target, minimumConfidence, race.Token), race.Token);
        var recognition = Task.Run(() => RecognizeAsync(frame, area, upscale, target,
            minimumConfidence, candidates.FirstOrDefault(), race.Token), race.Token);
        try
        {
            var (reading, fromOcr) = await RaceAsync(visual, recognition,
                r => OcrVisualMemory.IsConfident(Select(r, target), minimumConfidence), ct);
            ct.ThrowIfCancellationRequested();
            race.Cancel();
            if (fromOcr)
            {
                var selected = Select(reading, target);
                if (selected is not null)
                    _memory.Observe(key, frame, area, selected, target is not null, minimumConfidence);
            }
            _log?.Debug("OCR 并行竞速：{Source} 返回 {Context}。", fromOcr ? "OCR" : "区域比对", context);
            return reading;
        }
        finally
        {
            race.Cancel();
            // 不等待败者；已进入 Paddle 原生推理的任务不能强杀，完成后只观察异常并释放资源。
            _ = DrainAsync(visual, recognition, race);
        }
    }

    internal static async Task<(OcrReadout Reading, bool FromOcr)> RaceAsync(Task<OcrReadout?> visual,
        Task<OcrReadout> recognition, Func<OcrReadout, bool> isConfident, CancellationToken ct = default)
    {
        var first = await Task.WhenAny((Task)visual, recognition).WaitAsync(ct);
        if (first == visual && await visual is { } match) return (match, false);
        OcrReadout reading;
        try { reading = await recognition.WaitAsync(ct); }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // 推理报错不能抢先否定仍在运行的视觉路径；两路都失败时原异常照常抛出。
            if (await visual.WaitAsync(ct) is { } recovered) return (recovered, false);
            throw;
        }
        if (isConfident(reading)) return (reading, true);
        if (await visual.WaitAsync(ct) is { } fallback) return (fallback, false);
        return (reading, true); // 保留低分/空读数，交给原有诊断与失败策略。
    }

    private static OcrReadout? Compare(CapturedFrame frame, OcrVisualSample[] candidates,
        string? target, float minimumConfidence, CancellationToken ct)
    {
        foreach (var sample in candidates)
            if (sample.CanReuse && OcrVisualMemory.IsConfident(Select(sample.Reading, target), minimumConfidence) &&
                sample.Matches(frame, ct: ct))
            {
                sample.Hit();
                return sample.Reading;
            }
        return null;
    }

    private async Task<OcrReadout> RecognizeAsync(CapturedFrame frame, NRect area, double upscale,
        string? target, float minimumConfidence, OcrVisualSample? learned, CancellationToken ct)
    {
        var search = OcrPixelArea.From(area, frame);
        if (learned is not null && learned.LearnedArea != search &&
            (target is not null || learned.Matches(frame, ignore: learned.LearnedArea, ct: ct)))
        {
            // 宽高来自真实词框；内容扩展到裁剪区域外时，边界守卫强制使用完整搜索区域。
            var local = await _ocr(frame, learned.LearnedArea.Normalize(frame), upscale, ct);
            if (OcrVisualMemory.IsConfident(Select(local, target), minimumConfidence)) return local;
            ct.ThrowIfCancellationRequested();
            _log?.Debug("学习区域未确认目标，扩大 OCR 搜索范围。");
        }
        return await _ocr(frame, area, upscale, ct);
    }

    private static OcrReadout? Select(OcrReadout reading, string? target)
    {
        if (target is null) return reading;
        var line = OcrEvidence.FindTarget(reading, target);
        return line is null ? null : new(line.Text, [line]);
    }

    private static async Task DrainAsync(Task visual, Task recognition, CancellationTokenSource race)
    {
        try { await Task.WhenAll(visual, recognition); }
        catch (Exception) { /* 败者不影响已返回的可信结果，防止未观察任务异常。 */ }
        finally { race.Dispose(); }
    }
}
