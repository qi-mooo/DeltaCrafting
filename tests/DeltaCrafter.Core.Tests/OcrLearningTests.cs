using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class OcrLearningTests
{
    private static readonly NRect Area = new() { W = 1, H = 1 };
    private static readonly OcrReadout Empty = new("", []);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Either_trusted_path_returns_without_waiting_for_the_other(bool visualFirst)
    {
        var visual = new TaskCompletionSource<OcrReadout?>();
        var ocr = new TaskCompletionSource<OcrReadout>();
        var race = LearnedOcrReader.RaceAsync(visual.Task, ocr.Task, r => !r.HasUncertainText);
        if (visualFirst) visual.SetResult(Reading());
        else ocr.SetResult(Reading());
        var result = await race.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(!visualFirst, result.FromOcr);
        Assert.Equal("生产", result.Reading.FullText);
        Assert.False(visualFirst ? ocr.Task.IsCompleted : visual.Task.IsCompleted);
        visual.TrySetResult(null);
        ocr.TrySetResult(Empty);
    }

    [Fact]
    public async Task Fast_visual_miss_does_not_win_the_race()
    {
        var ocr = new TaskCompletionSource<OcrReadout>();
        var race = LearnedOcrReader.RaceAsync(Task.FromResult<OcrReadout?>(null), ocr.Task, _ => true);
        Assert.False(race.IsCompleted);
        ocr.SetResult(Reading());
        Assert.True((await race).FromOcr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fast_untrusted_or_failed_ocr_still_allows_visual_confirmation(bool throws)
    {
        var visual = new TaskCompletionSource<OcrReadout?>();
        var ocr = throws ? Task.FromException<OcrReadout>(new InvalidOperationException("model"))
            : Task.FromResult(Reading(confidence: .3f));
        var race = LearnedOcrReader.RaceAsync(visual.Task, ocr, r => !r.HasUncertainText);
        Assert.False(race.IsCompleted);
        visual.SetResult(Reading());
        Assert.False((await race).FromOcr);
    }

    [Fact]
    public async Task Both_misses_preserve_low_confidence_and_model_failure()
    {
        var low = Reading(confidence: .3f);
        var miss = Task.FromResult<OcrReadout?>(null);
        Assert.Same(low, (await LearnedOcrReader.RaceAsync(miss, Task.FromResult(low), _ => false)).Reading);
        var failure = new InvalidOperationException("model unavailable");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LearnedOcrReader.RaceAsync(miss, Task.FromException<OcrReadout>(failure), _ => false)));
    }

    [Fact]
    public async Task Cancellation_does_not_wait_for_native_inference()
    {
        var visual = new TaskCompletionSource<OcrReadout?>();
        var ocr = new TaskCompletionSource<OcrReadout>();
        using var stop = new CancellationTokenSource();
        var race = LearnedOcrReader.RaceAsync(visual.Task, ocr.Task, _ => true, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => race);
        visual.SetResult(null);
        ocr.SetResult(Empty);
    }

    [Fact]
    public void Learning_requires_two_frames_and_never_reuses_empty_uncertain_or_blank_evidence()
    {
        var memory = new OcrVisualMemory();
        var frame = Frame();
        memory.Observe("target", frame, Area, Reading(), true, .9f);
        Assert.False(Assert.Single(memory.Candidates("target")).CanReuse);
        memory.Observe("target", frame, Area, Reading(), true, .9f);
        Assert.False(Assert.Single(memory.Candidates("target")).CanReuse);
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        Assert.True(Assert.Single(memory.Candidates("target")).CanReuse);

        foreach (var reading in new[] { Empty, Reading(confidence: .89f), Reading(confidence: float.NaN),
                     Reading() with { HasUnreadableCountdown = true },
                     new OcrReadout("生产", [new("生产", 100, 50)]) })
            memory.Observe("bad", frame, Area, reading, true, .9f);
        memory.Observe("bad", Frame(blank: true), Area, Reading(), true, .9f);
        var solidColor = Frame(blank: true);
        for (int p = 2; p < solidColor.Bgra.Length; p += 4) solidColor.Bgra[p] = 220;
        memory.Observe("bad", solidColor, Area, Reading(), true, .9f);
        Assert.Empty(memory.Candidates("bad"));
    }

    [Fact]
    public void Learned_size_tracks_word_bounds_and_font_height()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("small", Frame(), Area, Reading(), true, .9f);
        var small = Assert.Single(memory.Candidates("small")).LearnedArea;
        Assert.Equal(new OcrPixelArea(68, 28, 64, 44), small);
        memory.Observe("large", Frame(x: 70, y: 30, width: 100, height: 40), Area,
            Reading(x: 70, y: 30, width: 100, height: 40), true, .9f);
        var large = Assert.Single(memory.Candidates("large")).LearnedArea;
        Assert.Equal(new OcrPixelArea(46, 6, 148, 88), large);
    }

    [Fact]
    public void Visual_comparison_detects_one_changed_stroke_and_does_not_drift()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("region", Frame(), Area, Reading(), false, .9f);
        var sample = Assert.Single(memory.Candidates("region"));
        var changed = Frame();
        changed.Bgra[(50 * changed.Width + 90) * 4] ^= 128;
        Assert.False(sample.Matches(changed));
        var minor = Frame();
        for (int p = 0; p < minor.Bgra.Length; p += 4)
        {
            minor.Bgra[p]++;
            minor.Bgra[p + 3] = 0; // 捕获 alpha 不参与视觉身份。
        }
        Assert.True(sample.Matches(minor));
        for (int p = 0; p < minor.Bgra.Length; p += 4) minor.Bgra[p] += 6;
        Assert.False(sample.Matches(minor));
        Assert.Equal(Frame().Bgra[0], sample.Pixels[0]);
    }

    [Fact]
    public void Visual_comparison_tolerates_small_position_drift_but_rejects_new_stroke()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("region", Frame(), Area, Reading(), true, .9f);
        var sample = Assert.Single(memory.Candidates("region"));
        var shifted = Frame(x: 81, y: 42);
        Assert.True(sample.Matches(shifted));
        int changed = (50 * shifted.Width + 90) * 4;
        shifted.Bgra[changed] = shifted.Bgra[changed] < 128 ? (byte)255 : (byte)0;
        Assert.False(sample.Matches(shifted));
    }

    [Fact]
    public void Target_presence_ignores_other_content_but_complete_labels_guard_the_entire_region()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        memory.Observe("label", Frame(), Area, Reading(), false, .9f);
        var changed = Frame();
        changed.Bgra[(10 * changed.Width + 10) * 4] = 255;
        Assert.True(Assert.Single(memory.Candidates("target")).Matches(changed));
        Assert.False(Assert.Single(memory.Candidates("label")).Matches(changed));
    }

    [Fact]
    public void Geometry_scale_context_and_confidence_have_separate_learning()
    {
        var frame = Frame();
        string key = OcrVisualMemory.Key(frame, Area, 2, "navigation:12", "生产", .9f);
        Assert.NotEqual(key, OcrVisualMemory.Key(new(480, 240, new byte[480 * 240 * 4]), Area, 2, "navigation:12", "生产", .9f));
        Assert.NotEqual(key, OcrVisualMemory.Key(frame, new() { X = .1, W = .9, H = 1 }, 2, "navigation:12", "生产", .9f));
        Assert.NotEqual(key, OcrVisualMemory.Key(frame, Area, 1.5, "navigation:12", "生产", .9f));
        Assert.NotEqual(key, OcrVisualMemory.Key(frame, Area, 2, "navigation:13", "生产", .9f));
        Assert.NotEqual(key, OcrVisualMemory.Key(frame, Area, 2, "navigation:12", "中止", .9f));
        Assert.NotEqual(key, OcrVisualMemory.Key(frame, Area, 2, "navigation:12", "生产", .8f));
    }

    [Fact]
    public void Samples_remain_reusable_for_seven_days_and_two_verified_misses_retire_old_appearance()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        var sample = Assert.Single(memory.Candidates("target"));
        Assert.True(sample.CanReuse);
        Assert.True((sample with { VerifiedAt = DateTimeOffset.UtcNow.AddDays(-6) }).CanReuse);
        Assert.False((sample with { VerifiedAt = DateTimeOffset.UtcNow.AddDays(-8) }).CanReuse);
        memory.Observe("target", TintedFrame(10), Area, Reading(), true, .9f);
        var retained = Assert.Single(memory.Candidates("target"), s => ReferenceEquals(s.Pixels, sample.Pixels));
        Assert.Equal(1, retained.ReuseFailures);
        Assert.True(retained.CanReuse);
        memory.Observe("target", TintedFrame(20), Area, Reading(), true, .9f);
        Assert.DoesNotContain(memory.Candidates("target"), s => ReferenceEquals(s.Pixels, sample.Pixels));
        Assert.Equal(1, memory.Snapshot().ReusableSamples);
    }

    [Fact]
    public void Visual_hits_do_not_expire_a_sample_before_the_failure_limit()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        var sample = Assert.Single(memory.Candidates("target"));
        Assert.True(sample.CanReuse);
    }

    [Fact]
    public void Saved_failure_count_survives_restart()
    {
        string path = Path.Combine(Path.GetTempPath(), "ocr-learning-" + Guid.NewGuid(), "samples.json");
        try
        {
            var memory = new OcrVisualMemory(path);
            string key = OcrVisualMemory.Key(Frame(), Area, 2, "probe", "生产", .9f);
            memory.Observe(key, Frame(), Area, Reading(), true, .9f);
            memory.Observe(key, Frame(), Area, Reading(), true, .9f);
            memory.Observe(key, TintedFrame(10), Area, Reading(), true, .9f);
            var restored = new OcrVisualMemory(path);
            Assert.Single(restored.Candidates(key), s => s.ReuseFailures == 1);
            Assert.Equal(1, restored.Snapshot().TotalSamples);
            Assert.Equal(1, restored.Snapshot().ReusableSamples);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task Restart_reuses_local_samples_without_waiting_for_ocr()
    {
        string path = Path.Combine(Path.GetTempPath(), "ocr-learning-" + Guid.NewGuid(), "samples.json");
        try
        {
            var teacher = new LearnedOcrReader((_, _, _, _) => Task.FromResult(Reading()), path);
            await teacher.ReadAsync(Frame(), Area, 2, "probe", target: "生产");
            await teacher.ReadAsync(Frame(), Area, 2, "probe", target: "生产");
            var never = new TaskCompletionSource<OcrReadout>();
            var restored = new LearnedOcrReader((_, _, _, ct) => never.Task.WaitAsync(ct), path);
            var result = await restored.ReadAsync(Frame(), Area, 2, "probe", target: "生产")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("生产", result.FullText);
            Assert.Equal(.97f, result.Lines[0].Confidence);
            never.SetResult(Empty);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("version")]
    [InlineData("pixels")]
    [InlineData("bounds")]
    [InlineData("null")]
    public void Incompatible_or_malformed_saved_samples_are_never_reused(string damage)
    {
        string path = Path.Combine(Path.GetTempPath(), "ocr-learning-" + Guid.NewGuid(), "samples.json");
        try
        {
            var memory = new OcrVisualMemory(path);
            string key = OcrVisualMemory.Key(Frame(), Area, 2, "probe", "生产", .9f);
            memory.Observe(key, Frame(), Area, Reading(), true, .9f);
            memory.Observe(key, Frame(), Area, Reading(), true, .9f);
            var store = new JsonStoreBrick();
            var saved = store.Load<OcrVisualMemory.SavedMemory>(path);
            saved = damage switch
            {
                "machine" => saved with { Machine = saved.Machine + "-other" },
                "version" => saved with { Version = -1 },
                "pixels" => saved with { Samples = [saved.Samples[0] with { Pixels = [0] }] },
                "bounds" => saved with { Samples = [saved.Samples[0] with { LearnedArea = new(-1, 0, 10, 10) }] },
                _ => saved with { Samples = null! },
            };
            store.Save(path, saved);
            Assert.Empty(new OcrVisualMemory(path).Candidates(key));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task Cancelled_paddle_request_does_not_load_native_runtime()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PaddleOcrBrick().ReadAsync(Frame(), cancellationToken: stop.Token));
    }

    [Fact]
    public async Task Ocr_learns_crop_then_expands_when_text_grows_outside_it()
    {
        var calls = new List<OcrPixelArea>();
        var next = Reading();
        var reader = new LearnedOcrReader((frame, area, _, _) =>
        {
            calls.Add(OcrPixelArea.From(area, frame));
            return Task.FromResult(next);
        });
        await reader.ReadAsync(Frame(), Area, 2, "button");
        await reader.ReadAsync(Frame(), Area, 2, "button");
        Assert.Equal(new OcrPixelArea(0, 0, 240, 120), calls[0]);
        Assert.Equal(new OcrPixelArea(68, 28, 64, 44), calls[1]);

        next = Reading("生产中", width: 100);
        await reader.ReadAsync(Frame(width: 100), Area, 2, "button");
        Assert.Equal(new OcrPixelArea(0, 0, 240, 120), calls[^1]);
        await reader.ReadAsync(Frame(width: 100), Area, 2, "button");
        Assert.Equal(new OcrPixelArea(68, 28, 124, 44), calls[^1]);
    }

    [Fact]
    public async Task Moved_target_retries_full_search_and_learns_its_new_position()
    {
        var calls = new List<OcrPixelArea>();
        bool moved = false;
        var reader = new LearnedOcrReader((frame, area, _, _) =>
        {
            var crop = OcrPixelArea.From(area, frame);
            calls.Add(crop);
            return Task.FromResult(!moved ? Reading() : crop.Right < 210 ? Empty : Reading(x: 170));
        });
        await reader.ReadAsync(Frame(), Area, 2, "probe", target: "生产");
        await reader.ReadAsync(Frame(), Area, 2, "probe", target: "生产");
        moved = true;
        var result = await reader.ReadAsync(Frame(x: 170), Area, 2, "probe", target: "生产");
        Assert.Equal(190, result.Lines[0].CenterX);
        Assert.Equal(new OcrPixelArea(68, 28, 64, 44), calls[^2]);
        Assert.Equal(new OcrPixelArea(0, 0, 240, 120), calls[^1]);
        await reader.ReadAsync(Frame(x: 170), Area, 2, "probe", target: "生产");
        Assert.Equal(new OcrPixelArea(158, 28, 64, 44), calls[^1]);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"version\":1,\"samples\":null}")]
    public async Task Broken_cache_falls_back_to_real_ocr(string contents)
    {
        string path = Path.Combine(Path.GetTempPath(), "ocr-learning-" + Guid.NewGuid(), "samples.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await File.WriteAllTextAsync(path, contents);
            int calls = 0;
            var reader = new LearnedOcrReader((_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(Reading());
            }, path);
            Assert.Equal("生产", (await reader.ReadAsync(Frame(), Area, 2, "probe")).FullText);
            Assert.Equal(1, calls);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void Memory_limits_entries_and_keeps_distinct_button_states()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("button", Frame(), Area, Reading(), false, .9f);
        memory.Observe("button", Frame(width: 80), Area, Reading("一键补齐", width: 80), false, .9f);
        Assert.Equal(2, memory.Candidates("button").Length);
        for (int i = 0; i < 100; i++)
            memory.Observe("probe" + i, Frame(), Area, Reading(), true, .9f);
        Assert.Empty(memory.Candidates("button"));
        Assert.Empty(memory.Candidates("probe0"));
        Assert.Single(memory.Candidates("probe99"));
    }

    [Fact]
    public void Confirmation_accumulates_across_appearance_changes_and_small_ocr_box_jitter()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        memory.Observe("target", TintedFrame(20), Area, Reading(x: 83, y: 42), true, .9f);

        Assert.Equal(2, memory.Candidates("target").Length);
        Assert.All(memory.Candidates("target"), s => Assert.True(s.CanReuse));
        var status = memory.Snapshot();
        Assert.Equal(1, status.TotalSamples);
        Assert.Equal(1, status.ConfirmedSamples);
        Assert.Equal(1, status.ReusableSamples);
        Assert.Equal(new OcrPixelArea(71, 30, 64, 44), memory.Candidates("target")[0].LearnedArea);
    }

    [Fact]
    public void Same_frame_with_changed_pixels_does_not_confirm_learning()
    {
        var memory = new OcrVisualMemory();
        var frame = Frame();
        memory.Observe("target", frame, Area, Reading(), true, .9f);
        frame.Bgra[(50 * frame.Width + 90) * 4] ^= 128;
        memory.Observe("target", frame, Area, Reading(), true, .9f);
        Assert.Equal(1, memory.Snapshot().TotalSamples);
        Assert.Equal(0, memory.Snapshot().ConfirmedSamples);
    }

    [Fact]
    public void Nearby_distinct_locations_do_not_share_confirmation()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("target", Frame(), Area, Reading(), true, .9f);
        memory.Observe("target", Frame(x: 110), Area, Reading(x: 110), true, .9f);
        Assert.Equal(2, memory.Snapshot().TotalSamples);
        Assert.Equal(0, memory.Snapshot().ConfirmedSamples);
    }

    [Fact]
    public void Contradictory_ocr_for_identical_pixels_requires_confirmation_again()
    {
        var memory = new OcrVisualMemory();
        memory.Observe("target", Frame(), Area, Reading(), false, .9f);
        memory.Observe("target", Frame(), Area, Reading(), false, .9f);
        Assert.Equal(1, memory.Snapshot().ReusableSamples);
        memory.Observe("target", Frame(), Area, Reading("中止"), false, .9f);
        Assert.Equal("中止", Assert.Single(memory.Snapshot().Entries).Text);
        Assert.Equal(0, memory.Snapshot().ReusableSamples);
    }

    [Fact]
    public async Task Looking_for_a_button_on_other_screens_does_not_forget_it()
    {
        OcrReadout next = Reading();
        bool blockOcr = false;
        var pending = new TaskCompletionSource<OcrReadout>();
        var reader = new LearnedOcrReader((_, _, _, ct) =>
            blockOcr ? pending.Task.WaitAsync(ct) : Task.FromResult(next));
        await reader.ReadAsync(Frame(), Area, 2, "probe", target: "生产");
        await reader.ReadAsync(Frame(), Area, 2, "probe", target: "生产");
        next = Empty;
        for (int i = 0; i < 4; i++)
            Assert.Empty((await reader.ReadAsync(Frame(blank: true), Area, 2, "probe", target: "生产")).Lines);
        Assert.Equal(1, reader.LearningStatus.ReusableSamples);
        Assert.Equal(0, Assert.Single(reader.LearningStatus.Entries).ReuseFailures);

        blockOcr = true;
        var reused = await reader.ReadAsync(Frame(), Area, 2, "probe", target: "生产")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("生产", reused.FullText);
        pending.TrySetResult(Empty);
    }

    private static CapturedFrame TintedFrame(byte offset)
    {
        var frame = Frame();
        for (int p = 0; p < frame.Bgra.Length; p += 4)
            for (int channel = 0; channel < 3; channel++) frame.Bgra[p + channel] += offset;
        return frame;
    }

    private static OcrReadout Reading(string text = "生产", float confidence = .97f,
        int x = 80, int y = 40, int width = 40, int height = 20) => new(text,
        [new(text, x + width / 2.0, y + height / 2.0)
        { Confidence = confidence, Words = [new(text, x, y, width, height) { Confidence = confidence }] }]);

    private static CapturedFrame Frame(int x = 80, int y = 40, int width = 40, int height = 20, bool blank = false)
    {
        var pixels = new byte[240 * 120 * 4];
        for (int row = 0; row < 120; row++)
            for (int col = 0; col < 240; col++)
            {
                int p = (row * 240 + col) * 4;
                byte value = !blank && col >= x && col < x + width && row >= y && row < y + height &&
                    (col + row) % 3 == 0 ? (byte)220 : (byte)20;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = value;
                pixels[p + 3] = 255;
            }
        return new(240, 120, pixels);
    }
}
