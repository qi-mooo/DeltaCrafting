using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaCrafter.Core.L0;
using Serilog;

namespace DeltaCrafter.Core.L1;

/// <summary>本机 OCR 样本。只用真实 OCR 更新模板，视觉命中不自我训练，避免逐帧漂移。</summary>
internal sealed class OcrVisualMemory
{
    private const int FormatVersion = 1;
    private const int MaximumSamples = 96;
    private const int MaximumBytes = 8 * 1024 * 1024;
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
            var previous = _samples.LastOrDefault(s => s.Key == key && s.Matches(frame));
            int observations = 1;
            if (previous is not null)
            {
                bool same = SameReading(previous.Reading, reading);
                bool newFrame = previous.LastFrame is null ||
                    !previous.LastFrame.TryGetTarget(out var last) || !ReferenceEquals(last, frame);
                observations = same ? Math.Min(2, previous.Observations + (newFrame ? 1 : 0)) : 1;
                _samples.Remove(previous);
            }
            _samples.Add(new(key, frame.Width, frame.Height, image, learned, pixels, reading,
                observations, DateTimeOffset.UtcNow) { LastFrame = new(frame) });
            while (_samples.Count(s => s.Key == key) > 4)
                _samples.Remove(_samples.First(s => s.Key == key));
            while (_samples.Count > MaximumSamples || _samples.Sum(s => (long)s.Pixels.Length) > MaximumBytes)
                _samples.RemoveAt(0);
            _log?.Debug("OCR 学习：文字={Text}; 区域=({X},{Y},{W},{H}); 确认={Count}/2",
                reading.FullText, learned.X, learned.Y, learned.Width, learned.Height, observations);
            if (_path is null) return;
            try { _store.Save(_path, new SavedMemory(FormatVersion, Environment.MachineName, _samples)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Warning(ex, "无法保存 OCR 学习数据，本次继续使用内存样本：{Path}", _path);
            }
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
            Math.Abs(p.First.CenterX - p.Second.CenterX) <= 2 &&
            Math.Abs(p.First.CenterY - p.Second.CenterY) <= 2);

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
    int Observations, DateTimeOffset VerifiedAt)
{
    private int _hits;
    [JsonIgnore] public WeakReference<CapturedFrame>? LastFrame { get; init; }
    internal bool CanReuse => Observations >= 2 && Volatile.Read(ref _hits) < 32 &&
        DateTimeOffset.UtcNow - VerifiedAt < TimeSpan.FromDays(1);
    internal void Hit() => Interlocked.Increment(ref _hits);

    internal bool IsValid() => Key is { Length: 64 } && FrameWidth is > 0 and <= 16384 &&
        FrameHeight is > 0 and <= 16384 && ImageArea is not null && LearnedArea is not null &&
        ImageArea.Inside(FrameWidth, FrameHeight) && LearnedArea.Inside(FrameWidth, FrameHeight) &&
        ImageArea.Contains(LearnedArea) && Pixels is not null &&
        (long)ImageArea.Width * ImageArea.Height * 3 == Pixels.Length &&
        Pixels.Length <= 256 * 1024 * 3 && Observations is >= 1 and <= 2 &&
        VerifiedAt <= DateTimeOffset.UtcNow.AddMinutes(5) && OcrVisualMemory.IsConfident(Reading, .8f) &&
        Reading.Lines.All(l => l.Words.Count > 0 && l.CenterX >= ImageArea.X &&
            l.CenterY >= ImageArea.Y && l.CenterX < ImageArea.Right && l.CenterY < ImageArea.Bottom);

    /// <summary>原尺寸逐像素 RGB 比较，不缩略、不用全区平均相似度掩盖单个字的变化。
    /// ignore 用于 OCR 裁剪边界守卫：区域外出现新字或移动时，必须扩大搜索。</summary>
    internal bool Matches(CapturedFrame frame, OcrPixelArea? ignore = null, CancellationToken ct = default)
    {
        if (frame.Width != FrameWidth || frame.Height != FrameHeight) return false;
        long delta = 0, count = 0;
        for (int y = 0; y < ImageArea.Height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (int x = 0; x < ImageArea.Width; x++)
            {
                int fx = ImageArea.X + x, fy = ImageArea.Y + y;
                if (ignore is not null && fx >= ignore.X && fx < ignore.Right && fy >= ignore.Y && fy < ignore.Bottom)
                    continue;
                int source = (fy * frame.Width + fx) * 4, saved = (y * ImageArea.Width + x) * 3;
                for (int c = 0; c < 3; c++)
                {
                    int difference = Math.Abs(frame.Bgra[source + c] - Pixels[saved + c]);
                    if (difference > 6) return false;
                    delta += difference;
                    count++;
                }
            }
        }
        return delta <= count; // 平均通道差不超过 1，单点差不超过 6/255。
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
