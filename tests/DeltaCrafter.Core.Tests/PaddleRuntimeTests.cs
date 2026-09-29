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
    public async Task Actual_compound_bow_title_is_complete_in_weapon_area_at_both_resolutions()
    {
        // 183 失败截图只保留标题区域；还原其位置以验证生产流程所用的 ROI。
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "title-compound-bow.png"));
        using var original = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using (var destination = new Mat(original, new Rect(624, 144, crop.Width, crop.Height)))
            crop.CopyTo(destination);
        var configured = new NRect { X = .347, Y = .1472, W = .126, H = .0426 };
        var area = ItemTitleOcr.AreaFor("复合弓", configured);
        var ocr = new PaddleOcrBrick();
        foreach (int width in new[] { 1920, 2560 })
        {
            using var scaled = new Mat();
            Cv2.Resize(original, scaled, new Size(width, width * 9 / 16));
            using var canvas = new Mat();
            Cv2.CvtColor(scaled, canvas, ColorConversionCodes.BGR2BGRA);
            var pixels = new byte[canvas.Width * canvas.Height * 4];
            Marshal.Copy(canvas.Data, pixels, 0, pixels.Length);
            var frame = new CapturedFrame(canvas.Width, canvas.Height, pixels);
            var title = OcrMatchFilter.Filter(ItemTitleOcr.Normalize(await ocr.ReadAsync(frame, area)), ["复合弓"]);
            Assert.False(title.HasUncertainText);
            Assert.Equal("复合弓", title.FullText);
            Assert.True(CatalogNameResolver.Matches([new() { Name = "复合弓" }], title.FullText, "复合弓"));
        }
    }

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
