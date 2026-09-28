using System.Runtime.InteropServices;
using DeltaCrafter.Core.L0;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models.Local;

namespace DeltaCrafter.Core.L1;

/// <summary>离线中文/拉丁混排物品识别。模型按需加载，共享实例串行访问。</summary>
public sealed class PaddleOcrBrick
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<PaddleOcrAll> _engine = new(() =>
    {
        try
        {
            var engine = new PaddleOcrAll(LocalFullModels.ChineseV5, PaddleDevice.Mkldnn())
            { AllowRotateDetection = false, Enable180Classification = false };
            engine.Detector.MaxSize = null;
            return engine;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("PaddleOCR 离线模型加载失败，请检查完整客户端文件和 Visual C++ x64 运行库。已停止物品识别。", ex);
        }
    });

    public async Task<OcrReadout> ReadAsync(CapturedFrame frame, NRect area, double upscale = 2)
    {
        if (!double.IsFinite(upscale) || upscale <= 0)
            throw new ArgumentOutOfRangeException(nameof(upscale));
        await _gate.WaitAsync();
        try { return await Task.Run(() => Read(frame, area, upscale)); }
        catch (Exception ex)
        {
            throw new InvalidOperationException("PaddleOCR 物品识别失败，请检查完整客户端、离线模型及 Visual C++ x64 运行库。已停止本轮，请重试。", ex);
        }
        finally { _gate.Release(); }
    }

    private OcrReadout Read(CapturedFrame frame, NRect area, double upscale)
    {
        var (x, y, w, h) = PixelMapper.ToPixelRect(area, frame.Width, frame.Height);
        double scale = Math.Min(upscale, 2048.0 / Math.Max(w, h));
        int dw = Math.Max(1, (int)(w * scale)), dh = Math.Max(1, (int)(h * scale));
        var data = OcrImagePreprocessor.Prepare(frame, x, y, w, h, dw, dh);
        using var bgra = new Mat(dh, dw, MatType.CV_8UC4);
        Marshal.Copy(data, 0, bgra.Data, data.Length);
        using var bgr = new Mat();
        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
        var result = _engine.Value.Run(bgr);
        double sx = (double)dw / w, sy = (double)dh / h;
        // 不能静默丢弃低置信度倒计时，否则制造中会被误判成可领取。
        var lines = result.Regions.Where(r => !string.IsNullOrWhiteSpace(r.Text))
            .Select(r =>
            {
                var box = r.Rect.BoundingRect();
                return new OcrLine(r.Text, x + r.Rect.Center.X / sx, y + r.Rect.Center.Y / sy)
                {
                    Confidence = r.Score,
                    Words = [new OcrWordBox(r.Text, x + box.X / sx, y + box.Y / sy, box.Width / sx, box.Height / sy)],
                };
            }).OrderBy(r => r.CenterY).ThenBy(r => r.CenterX).ToArray();
        return OcrLineAssembler.Assemble(lines);
    }
}
