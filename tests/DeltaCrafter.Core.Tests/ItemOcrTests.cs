using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ItemOcrTests
{
    private static readonly CatalogItem[] Catalog = new[]
    {
        "DICH-9重型头盔", "DICH-1战术头盔", "DICH训练头盔", "特里克MAS2.0装甲",
        "全景红点瞄准镜", "侧置全景红点瞄准镜", "7.62×39mm AP", "7.62×39mm BP",
    }.Select(n => new CatalogItem { Name = n }).ToArray();

    [Theory]
    [InlineData("DICH-9 重型头盔", "DICH-9重型头盔")]
    [InlineData("特里克 MAS2．0 装甲", "特里克MAS2.0装甲")]
    [InlineData("7.62x39mm AP", "7.62×39mm AP")]
    public void Complete_names_resolve_with_layout_differences(string observed, string expected) =>
        Assert.Equal(expected, CatalogNameResolver.Resolve(Catalog, observed));

    [Theory]
    [InlineData("D ℃ H．9 重型头盔")]
    [InlineData("DICH-1重型头盔")]
    [InlineData("D6重型头盔")]
    [InlineData("D1CH-9重型头盔")]
    [InlineData("DICH-9")]
    [InlineData("特里克MAS2.0装印")]
    [InlineData("侧置全景红点")]
    public void Misreads_are_not_guessed_from_the_catalog(string observed) =>
        Assert.Null(CatalogNameResolver.Resolve(Catalog, observed));

    [Fact]
    public void Similar_names_and_duplicate_entries_cannot_confirm_a_selection()
    {
        Assert.False(CatalogNameResolver.Matches(Catalog, "侧置全景红点瞄准镜", "全景红点瞄准镜"));
        Assert.False(CatalogNameResolver.Matches(Catalog, "全景红点瞄准镜", "侧置全景红点瞄准镜"));
        Assert.False(CatalogNameResolver.Matches(Catalog, "7.62×39mm BP", "7.62×39mm AP"));
        Assert.Null(CatalogNameResolver.Resolve([Catalog[0], Catalog[0]], "DICH-9重型头盔"));
    }

    [Fact]
    public void Split_regions_merge_only_on_the_same_baseline_and_keep_low_confidence()
    {
        var result = OcrLineAssembler.Assemble([
            Region(".300", 120, 46, 49, .99f), Region("BLK", 175, 47, 40, .98f),
            Region("120", 58, 75, 34, .99f), Region("02:00:00", 120, 100, 95, .7f),
            Region("相邻卡片", 600, 46, 100, .99f),
        ]);
        Assert.Equal(4, result.Lines.Count);
        var name = Assert.Single(result.Lines, l => l.Text == ".300 BLK");
        Assert.Equal(.98f, name.Confidence);
        Assert.Equal(2, name.Words.Count);
        Assert.True(result.HasUncertainText); // 低置信度倒计时不能静默丢弃。
    }

    [Theory]
    [InlineData(.90f, false)]
    [InlineData(.89f, true)]
    [InlineData(float.NaN, true)]
    public void Confidence_gate_rejects_uncertain_readings(float score, bool expected)
    {
        var result = OcrLineAssembler.Assemble([Region("DICH-9", 10, 10, 90, score)]);
        Assert.Equal(expected, result.HasUncertainText);
    }

    [Fact]
    public void Preprocessing_preserves_color_source_and_outputs_opaque_grayscale()
    {
        var pixels = new byte[] { 31, 40, 50, 255, 63, 43, 31, 255 };
        var original = pixels.ToArray();
        var output = OcrImagePreprocessor.Prepare(new(2, 1, pixels), 0, 0, 2, 1, 4, 2);
        Assert.Equal(original, pixels);
        Assert.Equal(32, output.Length);
        for (int i = 0; i < output.Length; i += 4)
        {
            Assert.Equal(output[i], output[i + 1]);
            Assert.Equal(output[i], output[i + 2]);
            Assert.Equal(255, output[i + 3]);
        }
    }

    private static OcrLine Region(string text, double x, double y, double w, float score) =>
        new(text, x + w / 2, y + 9) { Confidence = score, Words = [new(text, x, y, w, 18)] };
}
