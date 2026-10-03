using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class CountdownOcrTests
{
    private static OcrReadout Reading(string text, float confidence = .98f) =>
        new(text, [new OcrLine(text, 50, 20) { Confidence = confidence }]);

    [Fact]
    public void Isolated_icon_does_not_lower_the_separate_timer_region_confidence()
    {
        var line = new OcrLine("8 剩余时间：07:59:48", 100, 30)
        {
            Confidence = .59f,
            Words = [new("8", 0, 10, 20, 20) { Confidence = .59f },
                new("剩余时间：07:59:48", 30, 10, 170, 20) { Confidence = .98f }],
        };
        Assert.True(CountdownOcr.TryRead(new(line.Text, [line]), out var value));
        Assert.Equal(new TimeSpan(7, 59, 48), value);
    }

    [Theory]
    [InlineData(.89f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Low_or_invalid_timer_confidence_is_rejected(float score) =>
        Assert.False(CountdownOcr.TryRead(Reading("07:59:48", score), out _));

    [Theory]
    [InlineData("07 5948")]
    [InlineData("剩余时间 07:75:48")]
    [InlineData("8")]
    [InlineData("")]
    public void Missing_or_invalid_timer_is_not_guessed(string text) =>
        Assert.False(CountdownOcr.TryRead(Reading(text), out _));

    [Fact]
    public void Conflicting_readings_are_rejected()
    {
        var readings = new OcrReadout("", [.. Reading("07:59:48").Lines, .. Reading("01:59:48").Lines]);
        Assert.False(CountdownOcr.TryRead(readings, out _));
    }

    [Fact]
    public void Two_frames_must_follow_elapsed_time_and_compensate_ocr_latency()
    {
        var check = new CountdownOcr();
        Assert.False(check.Observe(Reading("07:59:48"), 1000, 2300, out _));
        Assert.False(check.Observe(Reading("01:59:46"), 3000, 4300, out _));
        Assert.False(check.Observe(Reading("07:59:44"), 5000, 6300, out _));
        Assert.False(check.Observe(Reading("07:59:42", .5f), 7000, 8300, out _));
        Assert.True(check.Observe(Reading("07:59:40"), 9000, 10500, out var value));
        Assert.Equal(new TimeSpan(7, 59, 40) - TimeSpan.FromSeconds(1.5), value);
    }

    [Fact]
    public void Identical_capture_cannot_supply_two_votes()
    {
        var check = new CountdownOcr();
        Assert.False(check.Observe(Reading("07:59:48"), 1000, 2000, out _));
        Assert.False(check.Observe(Reading("07:59:48"), 1000, 3000, out _));
    }
}
