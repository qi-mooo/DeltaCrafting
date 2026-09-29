using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class OcrMatchFilterTests
{
    [Theory]
    [InlineData("ABCDE", 100, true)]
    [InlineData("ABCDZ", 80, true)]
    [InlineData("ABCZZ", 60, false)]
    [InlineData("ABZZZ", 40, false)]
    [InlineData("ABC", 60, false)]
    [InlineData("", 0, false)]
    public void Only_strictly_above_sixty_percent_checks_confidence(string text, double percent, bool kept)
    {
        Assert.Equal(percent, OcrMatchFilter.MatchPercent(text, "ABCDE"));
        var result = OcrMatchFilter.Filter(Readout(Line(text, .1f)), ["ABCDE"]);
        Assert.Equal(kept, result.HasUncertainText);
        Assert.Equal(kept ? text : "", result.FullText);
    }

    [Theory]
    [InlineData(.1f)]
    [InlineData(.99f)]
    [InlineData(float.NaN)]
    public void Unrelated_text_and_symbols_are_discarded_regardless_of_confidence(float score)
    {
        var raw = Readout(Line("*", score), Line("120", score), Line("□??fg", score),
            Line("高级护甲维修组合", score), Line("DICH-9重型头盔", .99f));
        var result = OcrMatchFilter.Filter(raw, ["DICH-9重型头盔"]);
        Assert.Equal("DICH-9重型头盔", result.FullText);
        Assert.False(result.HasUncertainText);
        Assert.Equal(raw.FullText, result.SourceText);
    }

    [Theory]
    [InlineData("DICH-9重型头盔")]
    [InlineData("DICH-9重型头盟")]
    [InlineData("DICH-1重型头盔")]
    public void Relevant_misreads_keep_the_original_text_and_low_confidence(string observed)
    {
        var result = OcrMatchFilter.Filter(Readout(Line(observed, .8f)), ["DICH-9重型头盔"]);
        Assert.True(result.HasUncertainText);
        Assert.Equal(observed, result.FullText);
    }

    [Fact]
    public void Similar_names_remain_ineligible_for_clicking_and_blk_still_needs_color()
    {
        var catalog = new CatalogItem[] { new() { Name = "全景红点瞄准镜" }, new() { Name = "侧置全景红点瞄准镜" } };
        var result = OcrMatchFilter.Filter(Readout(Line("侧置全景红点瞄准镜", .99f)), [catalog[0].Name]);
        Assert.NotEmpty(result.Lines);
        Assert.False(CatalogNameResolver.Matches(catalog, result.FullText, catalog[0].Name));
        var targets = OcrMatchFilter.CatalogTargets([new() { Name = BlkAmmoIdentity.Name(3) }, new() { Name = BlkAmmoIdentity.Name(5) }]);
        Assert.Equal(".300 BLK", Assert.Single(targets));
        Assert.Null(BlkAmmoIdentity.Grade(".300 BLK"));
    }

    [Fact]
    public void Empty_targets_do_not_accept_noise_and_empty_pages_keep_raw_scroll_evidence()
    {
        var raw = Readout(Line("完全无关的名称", .1f));
        var result = OcrMatchFilter.Filter(raw, []);
        Assert.Empty(result.Lines);
        Assert.False(result.HasUncertainText);
        Assert.Equal(raw.FullText, result.SourceText);
        Assert.Equal(FacilityPhase.Unknown, CollectFlow.Classify(result, new()).Phase);
    }

    [Theory]
    [InlineData("01:05:46")]
    [InlineData("01:05:□")]
    [InlineData("O2:l5:3O")]
    public void Countdown_candidates_still_require_confidence(string text)
    {
        var result = OcrMatchFilter.Filter(Readout(Line("高级护甲维修组合", .99f), Line(text, .4f)),
            ["高级护甲维修组合"], includeCountdown: true);
        Assert.True(result.HasUncertainText);
        Assert.Contains(result.Lines, l => l.Text == text);
    }

    [Theory]
    [InlineData("乱码□*")]
    [InlineData("010546")]
    public void Discarded_timer_area_garbage_cannot_turn_crafting_into_ready(string text)
    {
        var result = OcrMatchFilter.Filter(Readout(Line("高级护甲维修组合", .99f), Line(text, .1f, 90)),
            ["高级护甲维修组合"], true, line => line.CenterY > 70);
        if (text == "乱码□*") Assert.False(result.HasUncertainText);
        Assert.True(result.HasUnreadableCountdown);
        Assert.Equal(FacilityPhase.Unknown, CollectFlow.Classify(result, new()).Phase);
    }

    [Fact]
    public void Valid_countdown_idle_and_ready_states_survive_filtering()
    {
        string[] targets = ["高级护甲维修组合", "空闲中"];
        var crafting = OcrMatchFilter.Filter(Readout(Line(targets[0], .99f), Line("01:05:46", .99f)), targets, true);
        Assert.Equal(FacilityPhase.Crafting, CollectFlow.Classify(crafting, new()).Phase);
        var ready = OcrMatchFilter.Filter(Readout(Line(targets[0], .99f), Line("fg", .1f)), targets, true);
        Assert.Equal(FacilityPhase.ReadyToCollect, CollectFlow.Classify(ready, new()).Phase);
        var idle = OcrMatchFilter.Filter(Readout(Line("空闲中", .99f), Line("*", .1f)), targets, true);
        Assert.Equal(FacilityPhase.Idle, CollectFlow.Classify(idle, new()).Phase);
        Assert.True(OcrMatchFilter.Filter(Readout(Line("空闲中", .5f)), targets, true).HasUncertainText);
    }

    private static OcrReadout Readout(params OcrLine[] lines) => new(string.Join("\n", lines.Select(l => l.Text)), lines);
    private static OcrLine Line(string text, float score, double y = 20) => new(text, 100, y) { Confidence = score };
}
