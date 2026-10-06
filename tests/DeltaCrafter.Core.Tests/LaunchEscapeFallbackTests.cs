using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class LaunchEscapeFallbackTests
{
    [Fact]
    public void Loading_cannot_trigger_escape_by_accumulating_ocr_misses()
    {
        var fallback = new LaunchEscapeFallback();
        for (long time = 0; time < 60_000; time += 100)
            Assert.False(fallback.Observe(null, time));
        Assert.True(fallback.Observe(null, 60_000));
        Assert.Equal(0, fallback.Attempts); // Eligibility alone never sends/consumes an ESC.
    }

    [Fact]
    public void Page_appearing_during_final_capture_cancels_fallback_and_restarts_wait()
    {
        var fallback = new LaunchEscapeFallback();
        Assert.False(fallback.Observe(null, 0));
        Assert.True(fallback.Observe(null, 65_000));
        Assert.False(fallback.Observe(AnchorKeys.ModeSelectPlay, 67_000));
        Assert.False(fallback.Observe(null, 70_000));
        Assert.False(fallback.Observe(null, 129_999));
        Assert.True(fallback.Observe(null, 130_000));
        Assert.Equal(0, fallback.Attempts);
    }

    [Fact]
    public void Each_escape_waits_again_and_known_pages_do_not_restore_attempt_budget()
    {
        var fallback = new LaunchEscapeFallback();
        Assert.False(fallback.Observe(null, 0));
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            long now = attempt * 70_000L;
            Assert.True(fallback.Observe(null, now));
            fallback.RecordEscape(now);
            Assert.Equal(attempt, fallback.Attempts);
            Assert.False(fallback.Observe(null, now + 59_999));
        }
        Assert.False(fallback.Observe(null, 1_000_000));
        Assert.False(fallback.Observe(AnchorKeys.ModeExitMenu, 1_000_001));
        Assert.False(fallback.Observe(null, 1_000_002));
        Assert.False(fallback.Observe(null, 2_000_000));
    }

    [Fact]
    public void Brief_unknown_frames_between_known_pages_do_not_accumulate()
    {
        var fallback = new LaunchEscapeFallback();
        Assert.False(fallback.Observe(null, 0));
        Assert.False(fallback.Observe(AnchorKeys.PromoAnnounce, 50_000));
        Assert.False(fallback.Observe(null, 55_000));
        Assert.False(fallback.Observe(AnchorKeys.Safehouse, 110_000));
        Assert.False(fallback.Observe(null, 115_000));
        Assert.False(fallback.Observe(AnchorKeys.Lobby, 170_000));
        Assert.Equal(0, fallback.Attempts);
    }
}
