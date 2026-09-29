using System.Runtime.InteropServices;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using OpenCvSharp;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class WindowsOcrFactAttribute : FactAttribute
{
    public WindowsOcrFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "PaddleOCR 原生运行库需要 Windows x64。";
    }
}

public sealed class PaddleRuntimeTests
{
    [WindowsOcrFact]
    public async Task Actual_replenish_and_purchased_button_images_are_recognized()
    {
        var ocr = new PaddleOcrBrick();
        var kw = new StateKeywords();
        var targets = kw.ButtonProduce.Concat(kw.ButtonReplenish).Concat(kw.ButtonAbort).ToArray();
        foreach (var (file, expected) in new[] { ("button-produce.png", "生产"), ("button-replenish.png", "一键补齐") })
        {
            using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
            using var canvas = new Mat();
            Cv2.CvtColor(source, canvas, ColorConversionCodes.BGR2BGRA);
            var pixels = new byte[canvas.Width * canvas.Height * 4];
            Marshal.Copy(canvas.Data, pixels, 0, pixels.Length);
            var raw = await ocr.ReadAsync(new CapturedFrame(canvas.Width, canvas.Height, pixels),
                new NRect { W = 1, H = 1 });
            var filtered = OcrMatchFilter.Filter(raw, targets);
            Assert.False(filtered.HasUncertainText);
            Assert.Equal(expected, ScreenProbe.Normalize(filtered.FullText));
            Assert.Equal(expected == "生产", CraftStartFlow.LabelHits(filtered.FullText, kw.ButtonProduce));
            Assert.Equal(expected == "一键补齐", CraftStartFlow.LabelHits(filtered.FullText, kw.ButtonReplenish));
        }
    }

    [WindowsOcrFact]
    public async Task Bundled_model_and_native_runtime_recognize_model_number()
    {
        using var canvas = new Mat(100, 440, MatType.CV_8UC4, new Scalar(35, 35, 35, 255));
        Cv2.PutText(canvas, "DICH-9", new Point(24, 68), HersheyFonts.HersheySimplex,
            1.8, new Scalar(245, 245, 245, 255), 2, LineTypes.AntiAlias);
        var pixels = new byte[canvas.Width * canvas.Height * 4];
        Marshal.Copy(canvas.Data, pixels, 0, pixels.Length);
        var ocr = new PaddleOcrBrick();
        var result = await ocr.ReadAsync(new CapturedFrame(canvas.Width, canvas.Height, pixels),
            new NRect { W = 1, H = 1 }, 1);
        Assert.False(result.HasUncertainText);
        Assert.Contains(result.Lines, l => CatalogNameResolver.Canonical(l.Text) == "DICH-9");
    }
}
