using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ItemTitleOcrTests
{
    [Fact]
    public void Actual_blk_title_ignores_separate_asterisk_and_keeps_name_confidence()
    {
        // 183 的 2026-09-29 12:32:25 失败读数：星号比标题小，组行后独立成行。
        var raw = OcrLineAssembler.Assemble([
            Region(".300", .995f, 667, 166.5, 63.5, 26.5),
            Region("BLK", .991f, 729, 167, 64, 25),
            Region("*", .887f, 790, 170.5, 11.5, 12.5),
            Region("120", .999f, 807, 165.5, 56.5, 28.5),
        ]);
        Assert.True(raw.HasUncertainText);
        var title = ItemTitleOcr.Normalize(raw);
        Assert.Equal(".300 BLK", title.FullText);
        Assert.True(BlkAmmoIdentity.IsBareName(title.FullText));
        Assert.False(title.HasUncertainText);
        Assert.Equal(.991f, Assert.Single(title.Lines).Confidence);
        Assert.Contains(title.Lines[0].Words, w => w.Text == "120");
    }

    [Theory]
    [InlineData(".300 BLK * 120", ".300 BLK")]
    [InlineData(".300 BLK＊120", ".300 BLK")]
    [InlineData("7.62×39mm AP × 120", "7.62×39mm AP")]
    [InlineData("侧置全景红点瞄准镜 * 1", "侧置全景红点瞄准镜")]
    public void Quantity_suffix_is_removed_without_changing_item_identity(string text, string name)
    {
        var title = ItemTitleOcr.Normalize(Readout(Region(text, .99f)));
        Assert.Equal(name, title.FullText);
        Assert.False(title.HasUncertainText);
        Assert.False(CatalogNameResolver.Matches([new() { Name = "全景红点瞄准镜" }], title.FullText, "全景红点瞄准镜"));
    }

    [Theory]
    [InlineData(".300 BLK 120")]
    [InlineData("7.62×39mm AP")]
    [InlineData("DICH-9重型头盔")]
    [InlineData(".300 BLK * 12O")]
    [InlineData("01:05:□")]
    [InlineData("01:05:00 * 120")]
    [InlineData("*")]
    public void Uncertain_names_timers_and_unexplained_symbols_are_preserved(string text)
    {
        var raw = Readout(Region(text, .38f));
        Assert.Same(raw, ItemTitleOcr.Normalize(raw));
        Assert.True(raw.HasUncertainText);
    }

    [Theory]
    [InlineData(.89f, .99f)]
    [InlineData(.99f, .89f)]
    [InlineData(float.NaN, .99f)]
    public void Name_and_quantity_confidence_remain_strict(float nameScore, float quantityScore)
    {
        var title = ItemTitleOcr.Normalize(Readout(
            Region(".300 BLK", nameScore, 0), Region("*", .4f, 110), Region("120", quantityScore, 125)));
        Assert.Equal(".300 BLK", title.FullText);
        Assert.True(title.HasUncertainText);
    }

    [Fact]
    public void Low_confidence_combined_region_and_multiple_rows_are_not_excused()
    {
        Assert.True(ItemTitleOcr.Normalize(Readout(Region(".300 BLK * 120", .89f))).HasUncertainText);
        var rows = Readout(Region(".300 BLK", .99f), Region("* 120", .89f, 120, 50));
        Assert.Same(rows, ItemTitleOcr.Normalize(rows));
        Assert.True(rows.HasUncertainText);
    }

    private static OcrReadout Readout(params OcrLine[] lines) => new(string.Join("\n", lines.Select(l => l.Text)), lines);

    private static OcrLine Region(string text, float score, double x = 0, double y = 0,
        double width = 100, double height = 20) => new(text, x + width / 2, y + height / 2)
        { Confidence = score, Words = [new(text, x, y, width, height)] };
}
