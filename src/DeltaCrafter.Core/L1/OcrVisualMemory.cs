using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaCrafter.Core.L0;
using Serilog;

namespace DeltaCrafter.Core.L1;

/// <summary>OCR 区域学习的摘要；同一文字位置的不同外观合并计数，不暴露截图像素。</summary>
public sealed record OcrLearningStatus(
    int TotalSamples,
    int ConfirmedSamples,
    int ReusableSamples,
    IReadOnlyList<OcrLearningEntry> Entries)
{
    public static OcrLearningStatus Empty { get; } = new(0, 0, 0, []);
}

/// <summary>单个 OCR 学习区域的可读详情。</summary>
public sealed record OcrLearningEntry(
    string Text,
    int Observations,
    int ReuseFailures,
    bool Reusable,
    DateTimeOffset VerifiedAt,
    int X,
    int Y,
    int Width,
    int Height);

/// <summary>本机 OCR 样本。只用真实 OCR 更新模板，视觉命中不自我训练，避免逐帧漂移。</summary>
internal sealed class OcrVisualMemory
{
    private const int FormatVersion = 1;
    private const int MaximumSamples = 96;
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const int MaximumReuseFailures = 2;
    private readonly object _sync = new();
    private readonly List<OcrVisualSample> _samples = [];
    private readonly string? _path;
    private readonly ILogger? _log;
    private readonly JsonStoreBrick _store = new();

    internal OcrVisualMemory(string? path = null, ILogger? log = null)
    {
        _path = path;
        _log = log;
        if (path is null || !File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > MaximumBytes * 2)
                throw new InvalidDataException("OCR 学习文件过大");
            var data = _store.Load<SavedMemory>(path);
            if (data.Version != FormatVersion || data.Machine != Environment.MachineName)
            {
                _log?.Information("OCR 学习环境或格式已变化，将重新学习。");
                return;
            }
            if (data.Samples is null || data.Samples.Count > MaximumSamples ||
                data.Samples.Any(s => s is null || !s.IsValid()) ||
                data.Samples.Sum(s => (long)s.Pixels.Length) > MaximumBytes)
                throw new InvalidDataException("OCR 学习样本不完整");
            _samples.AddRange(data.Samples);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // 学习数据可重建，错误必须可见；绝不把损坏缓存当成成功识别。
            _log?.Warning(ex, "无法读取 OCR 学习数据，将使用实时 OCR 重新学习：{Path}", path);
        }
    }

    internal static string Key(CapturedFrame frame, NRect area, double upscale, string context,
        string? target, float minimumConfidence) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { frame.Width, frame.Height, Area = area, Upscale = upscale,
                Context = context, Target = target, MinimumConfidence = minimumConfidence }))));

    internal OcrVisualSample[] Candidates(string key)
    {
        lock (_sync) return _samples.Where(s => s.Key == key).Reverse().ToArray();
    }

    internal OcrLearningStatus Snapshot()
    {
        lock (_sync)
        {
            var groups = new List<List<OcrVisualSample>>();
            foreach (var sample in _samples.OrderByDescending(s => s.VerifiedAt))
            {
                var group = groups.FirstOrDefault(g => g[0].Key == sample.Key &&
                    SameReading(g[0].Reading, sample.Reading));
                if (group is null) groups.Add([sample]);
                else group.Add(sample);
            }
            var entries = groups.Select(g =>
                {
                    var latest = g[0];
                    return new OcrLearningEntry(latest.Reading.FullText, g.Max(s => s.Observations),
                        latest.ReuseFailures, g.Any(s => s.CanReuse), latest.VerifiedAt,
                        latest.LearnedArea.X, latest.LearnedArea.Y,
                        latest.LearnedArea.Width, latest.LearnedArea.Height);
                })
                .ToArray();
            return new(entries.Length, entries.Count(e => e.Observations >= 2),
                entries.Count(e => e.Reusable), entries);
        }
    }

    internal void Observe(string key, CapturedFrame frame, NRect searchArea, OcrReadout reading,
        bool targetOnly, float minimumConfidence)
    {
        if (!IsConfident(reading, minimumConfidence)) return;
        var search = OcrPixelArea.From(searchArea, frame);
        var learned = LearnArea(reading, search);
        if (learned is null) return;
        var image = targetOnly ? learned : search;
        if ((long)image.Width * image.Height > 256 * 1024) return;
        byte[] pixels = OcrVisualSample.Crop(frame, image);
        // 没有可区分笔画的近单色区域不作为模板。
        if (!HasDetail(pixels)) return;

        lock (_sync)
        {
            var related = _samples.Where(s => s.Key == key && SameReading(s.Reading, reading)).ToArray();
            var matching = _samples.Where(s => s.Key == key && s.Matches(frame)).ToArray();
            bool contradictory = matching.Any(s => !SameReading(s.Reading, reading));
            var previous = related.LastOrDefault();
            bool newFrame = previous is null || previous.LastFrame is null ||
                !previous.LastFrame.TryGetTarget(out var last) || !ReferenceEquals(last, frame);
            int observations = contradictory || previous is null ? 1 :
                Math.Min(2, related.Max(s => s.Observations) + (newFrame ? 1 : 0));

            // 确认次数来自不同帧的真实 OCR，不要求按钮背景、抗锯齿或悬停外观逐像素相同。
            // 保留少量外观模板共享确认次数；相同截图上的矛盾读数必须重新确认。
            foreach (var sample in matching) _samples.Remove(sample);
            foreach (var sample in related.Except(matching))
            {
                int index = _samples.IndexOf(sample);
                int failures = sample.ReuseFailures;
                // 只有 OCR 再次确认该文字仍在原位置，且所有已知外观都失配，才记复用失败。
                // 切换到其他页面、按钮变为另一种状态或 OCR 读不清，都不是模板失效证据。
                if (newFrame && matching.Length == 0 && sample.CanReuse)
                {
                    failures++;
                    if (failures >= MaximumReuseFailures)
                    {
                        _samples.RemoveAt(index);
                        _log?.Debug("OCR 确认目标仍在原位置，旧外观累计失配 {Count} 次，已撤销：{Text}。",
                            failures, sample.Reading.FullText);
                        continue;
                    }
                }
                _samples[index] = sample with { Observations = observations, ReuseFailures = failures };
            }
            _samples.Add(new(key, frame.Width, frame.Height, image, learned, pixels, reading,
                observations, DateTimeOffset.UtcNow) { LastFrame = new(frame) });
            while (_samples.Count(s => s.Key == key) > 4)
                _samples.Remove(_samples.First(s => s.Key == key));
            while (_samples.Count > MaximumSamples || _samples.Sum(s => (long)s.Pixels.Length) > MaximumBytes)
                _samples.RemoveAt(0);
            _log?.Debug("OCR 学习：文字={Text}; 区域=({X},{Y},{W},{H}); 确认={Count}/2",
                reading.FullText, learned.X, learned.Y, learned.Width, learned.Height, observations);
            PersistLocked();
        }
    }

    private void PersistLocked()
    {
        if (_path is null) return;
        try { _store.Save(_path, new SavedMemory(FormatVersion, Environment.MachineName, _samples)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning(ex, "无法保存 OCR 学习数据，本次继续使用内存样本：{Path}", _path);
        }
    }

    internal static bool IsConfident(OcrReadout? reading, float minimumConfidence) =>
        reading is { Lines.Count: > 0, HasUnreadableCountdown: false } &&
        !string.IsNullOrWhiteSpace(reading.FullText) && reading.Lines.All(l =>
            l is not null && !string.IsNullOrWhiteSpace(l.Text) && float.IsFinite(l.Confidence) &&
            l.Confidence >= minimumConfidence && l.Confidence <= 1 &&
            double.IsFinite(l.CenterX) && double.IsFinite(l.CenterY) && l.Words is not null &&
            l.Words.All(w => w is not null && !string.IsNullOrWhiteSpace(w.Text) &&
                double.IsFinite(w.Left) && double.IsFinite(w.Top) &&
                double.IsFinite(w.Width) && double.IsFinite(w.Height) && w.Width > 0 && w.Height > 0 &&
                (w.Confidence is null || float.IsFinite(w.Confidence.Value) &&
                    w.Confidence >= minimumConfidence && w.Confidence <= 1)));

    private static bool SameReading(OcrReadout first, OcrReadout second) =>
        first.FullText == second.FullText && first.Lines.Count == second.Lines.Count &&
        first.Lines.Zip(second.Lines).All(p => p.First.Text == p.Second.Text &&
            Math.Abs(p.First.CenterX - p.Second.CenterX) <= PositionTolerance(p.First, p.Second) &&
            Math.Abs(p.First.CenterY - p.Second.CenterY) <= PositionTolerance(p.First, p.Second));

    private static double PositionTolerance(OcrLine first, OcrLine second) =>
        Math.Clamp(Math.Min(first.Words.Max(w => w.Height), second.Words.Max(w => w.Height)) * .25, 2, 6);

    private static bool HasDetail(byte[] pixels)
    {
        // 分通道检查空间变化，不能把纯色按钮的 RGB 色差误当成文字笔画。
        for (int c = 0; c < 3; c++)
        {
            int low = 255, high = 0;
            for (int p = c; p < pixels.Length; p += 3)
            {
                low = Math.Min(low, pixels[p]);
                high = Math.Max(high, pixels[p]);
                if (high - low >= 24) return true;
            }
        }
        return false;
    }

    private static OcrPixelArea? LearnArea(OcrReadout reading, OcrPixelArea search)
    {
        if (reading.Lines.Any(l => l.Words.Count == 0 || l.CenterX < search.X ||
            l.CenterX >= search.Right || l.CenterY < search.Y || l.CenterY >= search.Bottom)) return null;
        var words = reading.Lines.SelectMany(l => l.Words).ToArray();
        double left = words.Min(w => w.Left), top = words.Min(w => w.Top);
        double right = words.Max(w => w.Left + w.Width), bottom = words.Max(w => w.Top + w.Height);
        // 拒绝越界词框；少量取整误差在边界夹取。保留随字号变化的留白，供检测器找完整文字。
        if (left < search.X - 2 || top < search.Y - 2 || right > search.Right + 2 || bottom > search.Bottom + 2)
            return null;
        double margin = Math.Max(6, words.Max(w => w.Height) * .6);
        int x = Math.Max(search.X, (int)Math.Floor(left - margin));
        int y = Math.Max(search.Y, (int)Math.Floor(top - margin));
        int r = Math.Min(search.Right, (int)Math.Ceiling(right + margin));
        int b = Math.Min(search.Bottom, (int)Math.Ceiling(bottom + margin));
        return r > x && b > y ? new(x, y, r - x, b - y) : null;
    }

    internal sealed record SavedMemory(int Version, string Machine, List<OcrVisualSample> Samples);
}

internal sealed record OcrPixelArea(int X, int Y, int Width, int Height)
{
    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;
    internal bool Inside(int width, int height) => X >= 0 && Y >= 0 && Width > 0 && Height > 0 &&
        (long)X + Width <= width && (long)Y + Height <= height;
    internal bool Contains(OcrPixelArea area) => area.X >= X && area.Y >= Y &&
        area.Right <= Right && area.Bottom <= Bottom;
    internal NRect Normalize(CapturedFrame frame) => new()
        { X = (double)X / frame.Width, Y = (double)Y / frame.Height,
            W = (double)Width / frame.Width, H = (double)Height / frame.Height };
    internal static OcrPixelArea From(NRect area, CapturedFrame frame)
    {
        var (x, y, w, h) = PixelMapper.ToPixelRect(area, frame.Width, frame.Height);
        return new(x, y, w, h);
    }
}

internal sealed record OcrVisualSample(string Key, int FrameWidth, int FrameHeight,
    OcrPixelArea ImageArea, OcrPixelArea LearnedArea, byte[] Pixels, OcrReadout Reading,
    int Observations, DateTimeOffset VerifiedAt, int ReuseFailures = 0)
{
    [JsonIgnore] public WeakReference<CapturedFrame>? LastFrame { get; init; }
    internal bool CanReuse => Observations >= 2 && ReuseFailures < 2 &&
        DateTimeOffset.UtcNow - VerifiedAt < TimeSpan.FromDays(7);
    internal bool IsValid() => Key is { Length: 64 } && FrameWidth is > 0 and <= 16384 &&
        FrameHeight is > 0 and <= 16384 && ImageArea is not null && LearnedArea is not null &&
        ImageArea.Inside(FrameWidth, FrameHeight) && LearnedArea.Inside(FrameWidth, FrameHeight) &&
        ImageArea.Contains(LearnedArea) && Pixels is not null &&
        (long)ImageArea.Width * ImageArea.Height * 3 == Pixels.Length &&
        Pixels.Length <= 256 * 1024 * 3 && Observations is >= 1 and <= 2 && ReuseFailures is >= 0 and < 2 &&
        VerifiedAt <= DateTimeOffset.UtcNow.AddMinutes(5) && OcrVisualMemory.IsConfident(Reading, .8f) &&
        Reading.Lines.All(l => l.Words.Count > 0 && l.CenterX >= ImageArea.X &&
            l.CenterY >= ImageArea.Y && l.CenterX < ImageArea.Right && l.CenterY < ImageArea.Bottom);

    /// <summary>在小范围平移中逐像素比较 RGB，容忍截图抗锯齿和一两像素取整误差，
    /// 但拒绝明显的新笔画。ignore 用于 OCR 裁剪边界守卫：区域外出现新字或移动时，必须扩大搜索。</summary>
    internal bool Matches(CapturedFrame frame, OcrPixelArea? ignore = null, CancellationToken ct = default)
    {
        if (frame.Width != FrameWidth || frame.Height != FrameHeight) return false;
        // 游戏 UI 的词框会因缩放取整和动画产生 1～2 像素漂移。尝试相邻位置，
        // 否则同一按钮每帧都会被逐像素比较判成不同外观，学习无法复用。
        for (int dy = -2; dy <= 2; dy++)
        for (int dx = -2; dx <= 2; dx++)
        {
            if (ImageArea.X + dx < 0 || ImageArea.Y + dy < 0 ||
                ImageArea.Right + dx > frame.Width || ImageArea.Bottom + dy > frame.Height)
                continue;
            long delta = 0, count = 0;
            bool hardDifference = false;
            for (int y = 0; y < ImageArea.Height && !hardDifference; y++)
            {
                ct.ThrowIfCancellationRequested();
                for (int x = 0; x < ImageArea.Width; x++)
                {
                    int fx = ImageArea.X + dx + x, fy = ImageArea.Y + dy + y;
                    if (ignore is not null && fx >= ignore.X && fx < ignore.Right && fy >= ignore.Y && fy < ignore.Bottom)
                        continue;
                    int source = (fy * frame.Width + fx) * 4, saved = (y * ImageArea.Width + x) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int difference = Math.Abs(frame.Bgra[source + c] - Pixels[saved + c]);
                        if (difference >= 80) { hardDifference = true; break; }
                        delta += difference;
                        count++;
                    }
                    if (hardDifference) break;
                }
            }
            // Allow small antialiasing/color drift, while a high contrast changed
            // stroke (or a broad state change) still forces a real OCR check.
            if (!hardDifference && count > 0 && delta <= count * 5) return true;
        }
        return false;
    }

    internal static byte[] Crop(CapturedFrame frame, OcrPixelArea area)
    {
        var pixels = new byte[area.Width * area.Height * 3];
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
            {
                int source = ((area.Y + y) * frame.Width + area.X + x) * 4;
                int destination = (y * area.Width + x) * 3;
                frame.Bgra.AsSpan(source, 3).CopyTo(pixels.AsSpan(destination, 3));
            }
        return pixels;
    }
}
