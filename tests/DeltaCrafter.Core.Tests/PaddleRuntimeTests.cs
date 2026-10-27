using System.Runtime.InteropServices;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using OpenCvSharp;
using Serilog;
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
    public async Task Empty_blueprint_requires_prompt_and_production_filter_on_actual_frame()
    {
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "navigation-production-empty.png"));
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(new(), new(), new PaddleOcrBrick(), new(), () => anchors, "", log);
        foreach (int width in new[] { 1920, 2560 })
        {
            var frame = FrameAt(original, width);
            var spec = anchors.Screen(AnchorKeys.ProductionEmpty);
            var texts = await probe.ReadScreenTextsAsync(frame, spec);
            Assert.True(ScreenProbe.MatchesScreenTexts(spec, texts), $"{width}px: {string.Join(" | ", texts)}");
            Assert.False(ScreenProbe.MatchesScreenTexts(spec, ["请放入生产图纸", ""]));
            Assert.False(ScreenProbe.MatchesScreenTexts(spec, ["", "全部类型"]));
        }
    }

    [WindowsOcrFact]
    public async Task Lobby_is_recognized_when_music_popup_covers_ready_button()
    {
        // 183 2026-10-09 06:14 失败现场，仅保留两个导航探针与被遮挡的按钮区域。
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "navigation-lobby-covered.png"));
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(new(), new(), new PaddleOcrBrick(), new(), () => anchors, "", log);
        var spec = anchors.Screen(AnchorKeys.Lobby);
        foreach (int width in new[] { 1920, 2560 })
        {
            var frame = FrameAt(original, width);
            var primaryOnly = new ScreenSpec { Probe = spec.Probe };
            Assert.False(ScreenProbe.MatchesScreenTexts(primaryOnly, await probe.ReadScreenTextsAsync(frame, primaryOnly)));
            var texts = await probe.ReadScreenTextsAsync(frame, spec);
            Assert.True(ScreenProbe.MatchesScreenTexts(spec, texts), $"{width}px: {string.Join(" | ", texts)}");
            Assert.Equal(2, texts.Count);
            // The top navigation also exists elsewhere. Without the lobby-only
            // shortcut, it must not skip loading or trigger a navigation click.
            var missingShortcut = frame with { Bgra = (byte[])frame.Bgra.Clone() };
            var (x, y, w, h) = PixelMapper.ToPixelRect(spec.AlternativeProbeGroups[0][0].Roi, width, width * 9 / 16);
            for (int row = y; row < y + h; ++row)
                Array.Clear(missingShortcut.Bgra, (row * width + x) * 4, w * 4);
            Assert.Empty(await probe.ReadScreenTextsAsync(missingShortcut, spec));
        }
    }

    [WindowsOcrFact]
    public async Task Actual_price_change_dialog_is_recognized_at_both_resolutions()
    {
        // 183 2026-10-07 补料失败现场，只保留弹窗标题，不含账号或支付信息。
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "navigation-replenish-price-change.png"));
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(new(), new(), new PaddleOcrBrick(), new(), () => anchors, "", log);
        foreach (int width in new[] { 1920, 2560 })
        {
            var frame = FrameAt(original, width);
            var spec = anchors.Screen(AnchorKeys.ReplenishPriceChange);
            var texts = await probe.ReadScreenTextsAsync(frame, spec);
            Assert.True(ScreenProbe.MatchesScreenTexts(spec, texts), $"{width}px: {string.Join(" | ", texts)}");
            Assert.False(ScreenProbe.MatchesScreenTexts(anchors.Screen(AnchorKeys.ReplenishPopup), texts));
            Assert.False(ScreenProbe.MatchesScreenTexts(anchors.Screen(AnchorKeys.Production), texts));
        }
    }

    [WindowsOcrFact]
    public async Task Actual_baldr_title_recovers_from_added_letter_and_isolated_noise_at_both_resolutions()
    {
        const string expected = "OLIGHT Baldr Pro R多功能手电";
        CatalogItem[] catalog = [new() { Name = expected }, new() { Name = "OLIGHT WARRIOR 3S战术手电" },
            new() { Name = "OLIGHT Odin S战术手电" }];
        // 183 在 2026-10-03 15:29 的失败画面，只保留标题带，不含账号信息。
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "title-baldr-flashlight.png"));
        using var original = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using (var destination = new Mat(original, new Rect(624, 144, crop.Width, crop.Height)))
            crop.CopyTo(destination);
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        var area = ItemTitleOcr.AreaFor(expected, anchors.Screen(AnchorKeys.Production).Roi(AnchorKeys.RoiDetailTitle));
        using var log = new LoggerConfiguration().CreateLogger();
        var ocr = new PaddleOcrBrick();
        var probe = new ScreenProbe(new(), new(), ocr, new(), () => anchors, "", log);
        foreach (int width in new[] { 1920, 2560 })
        {
            var frame = FrameAt(original, width);
            // 原生推理在不同 CPU 上可能首遍即读对；验证最终身份，不要求复现错字。
            string title = await probe.ReadFrameRoiAsync(frame, area, expected);
            Assert.True(CatalogNameResolver.Matches(catalog, title, expected), $"{width}px: {title}");
            Assert.False(CatalogNameResolver.Matches(catalog, title, catalog[1].Name));
            Assert.False(CatalogNameResolver.Matches(catalog, "OLIGHTE Baldr Pro R多功能手电", expected));
        }
    }

    [WindowsOcrFact]
    public async Task Actual_grip_replenish_button_uses_production_pipeline_at_both_resolutions()
    {
        // 183 在 2026-10-03 10:21 的失败画面，只保存操作按钮区域。
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "button-replenish-grip.png"));
        using var original = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using (var destination = new Mat(original, new Rect(1560, 906, crop.Width, crop.Height)))
            crop.CopyTo(destination);
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(new(), new(), new PaddleOcrBrick(), new(), () => anchors, "", log);
        foreach (int width in new[] { 1920, 2560 })
        {
            string label = await probe.ReadProductionButtonAsync(FrameAt(original, width),
                anchors.Screen(AnchorKeys.Production).Roi(AnchorKeys.RoiActionButton));
            Assert.Equal("一键补齐", label);
            Assert.True(CraftStartFlow.LabelHits(label, anchors.Keywords.ButtonReplenish));
            Assert.False(CraftStartFlow.LabelHits(label, anchors.Keywords.ButtonProduce));
            Assert.False(CraftStartFlow.LabelHits(label, anchors.Keywords.ButtonAbort));
        }
    }

    [WindowsOcrFact]
    public async Task Production_countdown_uses_paddle_and_retains_confidence_at_both_resolutions()
    {
        // 183 在 2026-10-01 14:35 的失败画面，仅保留倒计时区域。
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "countdown-started-blk.png"));
        using var original = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using (var destination = new Mat(original, new Rect(1526, 851, crop.Width, crop.Height)))
            crop.CopyTo(destination);
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(new(), new(), new PaddleOcrBrick(), new(), () => anchors, "", log);
        foreach (int width in new[] { 1920, 2560 })
        {
            var frame = FrameAt(original, width);
            foreach (double scale in new[] { 1.0, 2.0 })
            {
                var reading = await probe.ReadCountdownAsync(frame,
                    anchors.Screen(AnchorKeys.Production).Roi(AnchorKeys.RoiRemainingTime), scale);
                Assert.True(CountdownOcr.TryRead(reading, out var time));
                Assert.Equal(new TimeSpan(7, 59, 48), time);
            }
        }
    }

    [WindowsOcrFact]
    public async Task Navigation_probes_use_paddle_on_actual_screen_regions()
    {
        var anchors = new JsonStoreBrick().Load<AnchorTable>(Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));
        using var log = new LoggerConfiguration().CreateLogger();
        var probe = new ScreenProbe(new(), new(), new PaddleOcrBrick(), new(), () => anchors, "", log);
        foreach (string name in new[] { AnchorKeys.ModeSelectPlay, AnchorKeys.ModeExitMenu, AnchorKeys.Lobby,
                     AnchorKeys.Safehouse, AnchorKeys.PromoAnnounce, AnchorKeys.SpecOpsHome, AnchorKeys.Production })
        {
            // 只保留原帧里的锚点区域，其余画面置黑，不保存账号或用户桌面。
            using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"navigation-{name}.png"));
            foreach (int width in new[] { 1920, 2560 })
            {
                var texts = await probe.ReadScreenTextsAsync(FrameAt(original, width), anchors.Screen(name));
                Assert.True(ScreenProbe.MatchesScreenTexts(anchors.Screen(name), texts),
                    $"{name} {width}px: {string.Join(" | ", texts)}");
            }
        }
    }

    private static CapturedFrame FrameAt(Mat original, int width)
    {
        using var scaled = new Mat();
        Cv2.Resize(original, scaled, new Size(width, width * 9 / 16));
        using var bgra = new Mat();
        Cv2.CvtColor(scaled, bgra, ColorConversionCodes.BGR2BGRA);
        var pixels = new byte[bgra.Width * bgra.Height * 4];
        Marshal.Copy(bgra.Data, pixels, 0, pixels.Length);
        return new(bgra.Width, bgra.Height, pixels);
    }

    [WindowsOcrFact]
    public async Task Actual_long_flashlight_title_is_not_truncated_or_confused_with_other_flashlights()
    {
        const string expected = "OLIGHT WARRIOR 3S战术手电";
        CatalogItem[] catalog = [new() { Name = expected }, new() { Name = "OLIGHT Odin S战术手电" },
            new() { Name = "OLIGHT Baldr Pro R多功能手电" }];
        // 183 在 2026-10-01 01:36 的失败截图，只保存标题带，不包含账号或其它区域。
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "title-warrior-flashlight.png"));
        using var original = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using (var destination = new Mat(original, new Rect(624, 144, crop.Width, crop.Height)))
            crop.CopyTo(destination);
        var configured = new NRect { X = .347, Y = .1472, W = .126, H = .0426 };
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
            var truncated = await ocr.ReadAsync(frame, configured);
            Assert.False(CatalogNameResolver.Matches(catalog, truncated.FullText, expected));
            var title = OcrMatchFilter.Filter(ItemTitleOcr.Normalize(await ocr.ReadAsync(frame,
                ItemTitleOcr.AreaFor(expected, configured))), [expected]);
            Assert.False(title.HasUncertainText);
            Assert.True(CatalogNameResolver.Matches(catalog, title.FullText, expected));
            Assert.False(CatalogNameResolver.Matches(catalog, title.FullText, catalog[1].Name));
            Assert.False(CatalogNameResolver.Matches(catalog, "OLIGHT WARRIOR3", expected));
            Assert.False(CatalogNameResolver.Matches(catalog, "OLIGHT WARRI0R3S战术手电", expected));
        }
    }

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
        foreach (var (file, expected) in new[] { ("button-produce.png", "生产"),
            ("button-replenish.png", "一键补齐"), ("button-replenish-bow.png", "一键补齐") })
        {
            using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
            using var canvas = new Mat();
            Cv2.CvtColor(source, canvas, ColorConversionCodes.BGR2BGRA);
            var pixels = new byte[canvas.Width * canvas.Height * 4];
            Marshal.Copy(canvas.Data, pixels, 0, pixels.Length);
            var raw = await ocr.ReadAsync(new CapturedFrame(canvas.Width, canvas.Height, pixels),
                new NRect { W = 1, H = 1 });
            var filtered = OcrMatchFilter.Filter(raw, targets);
            Assert.False(filtered.HasTextBelowConfidence(ScreenProbe.ProductionButtonMinimumConfidence));
            if (file == "button-replenish-bow.png") Assert.True(filtered.HasUncertainText);
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
