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
        Assert.DoesNotContain(title.Lines[0].Words, w => w.Text == "120");
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
    [InlineData(.89f, .99f, true)]
    [InlineData(.99f, .89f, false)]
    [InlineData(float.NaN, .99f, true)]
    public void Only_name_confidence_is_used(float nameScore, float quantityScore, bool uncertain)
    {
        var title = ItemTitleOcr.Normalize(Readout(
            Region(".300 BLK", nameScore, 0), Region("*", .4f, 110), Region("120", quantityScore, 125)));
        Assert.Equal(".300 BLK", title.FullText);
        Assert.Equal(uncertain, title.HasUncertainText);
    }

    [Fact]
    public void Same_baseline_quantity_cannot_lower_name_confidence()
    {
        var raw = OcrLineAssembler.Assemble([
            Region(".300 BLK", .99f, 0), Region("*", .2f, 101, width: 10),
            Region("120", .3f, 112),
        ]);
        Assert.True(raw.HasUncertainText);
        var title = ItemTitleOcr.Normalize(raw);
        Assert.Equal(".300 BLK", title.FullText);
        Assert.False(title.HasUncertainText);
    }

    [Theory]
    [InlineData("AKM突击步枪")]
    [InlineData("PKM通用机枪")]
    [InlineData("M700狙击步枪")]
    [InlineData("M700狙击枪")]
    [InlineData("M14射手步枪")]
    [InlineData("P90冲锋枪")]
    [InlineData("FS-12霰弹枪")]
    [InlineData("M870散弹枪")]
    [InlineData("G18手枪")]
    [InlineData("复合弓")]
    public void Weapon_title_area_starts_at_640_and_scales_with_resolution(string name)
    {
        var configured = new NRect { X = .347, Y = .1472, W = .126, H = .0426 };
        var area = ItemTitleOcr.AreaFor(name, configured);
        Assert.Equal(640, (int)Math.Round(area.X * 1920));
        Assert.Equal(853, (int)Math.Round(area.X * 2560));
        Assert.Equal(1280, (int)Math.Round((area.X + area.W) * 1920));
        Assert.Equal(configured.Y, area.Y);
        Assert.Equal(configured.H, area.H);
        Assert.Equal(.347, configured.X);
        Assert.Equal(.126, configured.W);
    }

    [Theory]
    [InlineData("侧置全景红点瞄准镜")]
    [InlineData(".300 BLK")]
    [InlineData("特里克MAS2.0装甲")]
    [InlineData("骨架狙击枪托")]
    [InlineData("M4A1突击步枪长枪管")]
    [InlineData("OLIGHT WARRIOR 3S战术手电")]
    public void Titles_preserve_left_and_vertical_calibration_while_extending_right_for_long_names(string name)
    {
        var configured = new NRect { X = .347, Y = .1472, W = .126, H = .0426 };
        var area = ItemTitleOcr.AreaFor(name, configured);
        Assert.Equal(configured.X, area.X);
        Assert.Equal(configured.Y, area.Y);
        Assert.Equal(configured.H, area.H);
        Assert.Equal(1280, (int)Math.Round((area.X + area.W) * 1920));
        Assert.Equal(1707, (int)Math.Round((area.X + area.W) * 2560));
    }

    [Fact]
    public void An_already_wider_weapon_calibration_is_not_shrunk()
    {
        var configured = new NRect { X = .30, Y = .2, W = .40, H = .04 };
        var area = ItemTitleOcr.AreaFor("复合弓", configured);
        Assert.Same(configured, area);
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
