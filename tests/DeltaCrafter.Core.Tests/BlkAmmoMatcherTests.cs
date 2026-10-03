using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class BlkAmmoMatcherTests
{
    private static readonly NRect FullArea = new() { W = 1, H = 1 };

    [Theory]
    [InlineData(FacilityKey.Workbench, ".300BLK五级弹", ".300BLK", true)]
    [InlineData(FacilityKey.Workbench, ".300 blk 五级弹", "", true)]
    [InlineData(FacilityKey.Workbench, ".300BLK5级弹", "", true)]
    [InlineData(FacilityKey.Workbench, ".300BLK", ".300BLK五级弹", true)]
    [InlineData(FacilityKey.Workbench, ".300BLK", ".300BLK", false)]
    [InlineData(FacilityKey.Workbench, ".300BLK SUB-3", ".300BLK SUB-3", true)]
    [InlineData(FacilityKey.Workbench, ".300BLK SUB-4", ".300BLK SUB-4", true)]
    [InlineData(FacilityKey.TechCenter, ".300BLK五级弹", ".300BLK", false)]
    [InlineData(FacilityKey.Workbench, "自定义物品", "自定义物品", false)]
    public void Grade_background_rule_is_scoped_to_workbench_blk_ammunition(
        FacilityKey facility, string displayName, string searchName, bool expected)
    {
        Assert.Equal(expected, BlkAmmoMatcher.AppliesTo(facility, displayName, searchName));
    }

    [Theory]
    [InlineData(3, 31, 43, 63)]
    [InlineData(4, 51, 33, 64)]
    [InlineData(5, 50, 40, 31)]
    public void Each_grade_requires_its_own_color(int grade, int r, int g, int b)
    {
        var frame = Screenshot();
        Fill(frame, 26, 22, 71, 71, r, g, b);
        Assert.NotNull(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea, grade: grade));
        foreach (int other in new[] { 3, 4, 5 }.Where(g => g != grade))
            Assert.Null(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea, grade: other));
    }

    [Fact]
    public void Paddle_split_name_regions_keep_the_color_attached_to_the_correct_row()
    {
        var regions = NameLine().Words.Select(w => new OcrLine(w.Text,
            w.Left + w.Width / 2, w.Top + w.Height / 2) { Words = [w] });
        var readout = OcrLineAssembler.Assemble(regions);
        Assert.Single(readout.Lines);
        Assert.NotNull(BlkAmmoMatcher.Find(Screenshot(), readout.Lines, FullArea));
    }

    [Theory]
    [InlineData(.75)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Dark_gold_row_is_recognized_at_multiple_scales(double scale)
    {
        var frame = Scale(Screenshot(), scale);
        var candidate = BlkAmmoMatcher.Find(frame, [NameLine(scale)], FullArea, requireSelected: true);

        Assert.NotNull(candidate);
        Assert.InRange(candidate.X, 160 * scale, 175 * scale);
        Assert.InRange(candidate.Icon.Left, 24 * scale, 28 * scale);
    }

    [Theory]
    [InlineData(31, 43, 63)] // 蓝色
    [InlineData(51, 33, 64)] // 紫色
    [InlineData(35, 56, 42)] // 绿色
    [InlineData(52, 52, 52)] // 灰色
    [InlineData(65, 28, 29)] // 红色
    public void Same_text_and_selected_border_with_other_backgrounds_do_not_match(int r, int g, int b)
    {
        var frame = Screenshot();
        Fill(frame, 24, 20, 75, 75, r, g, b);

        Assert.Null(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea));
    }

    [Fact]
    public void Gold_object_on_blue_background_cannot_stand_in_for_gold_icon_background()
    {
        var frame = Screenshot();
        Fill(frame, 24, 20, 75, 75, 31, 43, 63);
        Fill(frame, 42, 40, 36, 26, 140, 110, 60);

        Assert.Null(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea));
    }

    [Fact]
    public void Quantity_merged_into_ocr_line_does_not_shift_icon_search()
    {
        var name = NameLine();
        var merged = new OcrLine("120 .300 BLK", 130, 65)
        {
            Words = [new OcrWordBox("120", 58, 72, 34, 18), .. name.Words],
        };

        var candidate = BlkAmmoMatcher.Find(Screenshot(), [merged], FullArea);

        Assert.NotNull(candidate);
        Assert.Equal(167.5, candidate.X);
        Assert.Equal(55, candidate.Y);
    }

    [Fact]
    public void Name_without_word_coordinates_is_not_enough_to_assign_a_color()
    {
        Assert.Null(BlkAmmoMatcher.Find(Screenshot(), [new OcrLine(".300 BLK", 167, 55)], FullArea));
    }

    [Fact]
    public void Gold_background_without_matching_name_does_not_match()
    {
        var wrongName = new OcrLine("7.62×39mm AP", 167, 55)
        {
            Words = [new OcrWordBox("7.62×39mm AP", 120, 46, 95, 18)],
        };
        Assert.Null(BlkAmmoMatcher.Find(Screenshot(), [wrongName], FullArea));
    }

    [Fact]
    public void Gold_row_without_selection_border_can_be_found_but_not_confirmed_selected()
    {
        var frame = Screenshot();
        RemoveBorder(frame);

        Assert.NotNull(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea));
        Assert.Null(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea, requireSelected: true));
    }

    [Fact]
    public void White_icon_border_is_not_the_row_selection_border()
    {
        var frame = Screenshot();
        RemoveBorder(frame);
        Fill(frame, 24, 20, 75, 2, 220, 220, 220);
        Fill(frame, 24, 93, 75, 2, 220, 220, 220);
        Fill(frame, 24, 20, 2, 75, 220, 220, 220);

        Assert.Null(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea, requireSelected: true));
    }

    [Fact]
    public void Adjacent_gold_row_cannot_supply_color_to_the_wrong_name()
    {
        var frame = StackRows(Screenshot(), Screenshot());
        Fill(frame, 24, 20, 75, 75, 31, 43, 63);

        Assert.Null(BlkAmmoMatcher.Find(frame, [NameLine()], FullArea));
        var candidate = BlkAmmoMatcher.Find(frame, [NameLine(), NameLine(offsetY: 111)], FullArea);
        Assert.NotNull(candidate);
        Assert.Equal(166, candidate.Y);
    }

    [Fact]
    public void Two_gold_candidates_are_ambiguous()
    {
        Assert.Null(BlkAmmoMatcher.Find(StackRows(Screenshot(), Screenshot()),
            [NameLine(), NameLine(offsetY: 111)], FullArea));
    }

    [Fact]
    public void Different_selected_row_does_not_confirm_the_gold_row()
    {
        var gold = Screenshot();
        RemoveBorder(gold);
        var blue = Screenshot();
        Fill(blue, 24, 20, 75, 75, 31, 43, 63);

        Assert.Null(BlkAmmoMatcher.Find(StackRows(gold, blue),
            [NameLine(), NameLine(offsetY: 111)], FullArea, requireSelected: true));
    }

    [Fact]
    public void Clipped_icon_is_rejected()
    {
        var clippedArea = new NRect { Y = 30.0 / 111, W = 1, H = 81.0 / 111 };
        Assert.Null(BlkAmmoMatcher.Find(Screenshot(), [NameLine()], clippedArea));
    }

    [Fact]
    public void Scroll_signature_detects_color_changes_even_with_identical_names()
    {
        var gold = Screenshot();
        var blue = Screenshot();
        Fill(blue, 24, 20, 75, 75, 31, 43, 63);

        Assert.NotEqual(BlkAmmoMatcher.ViewSignature(gold, FullArea),
            BlkAmmoMatcher.ViewSignature(blue, FullArea));
        Assert.Equal(BlkAmmoMatcher.ViewSignature(gold, FullArea),
            BlkAmmoMatcher.ViewSignature(Screenshot(), FullArea));
    }

    private static OcrLine NameLine(double scale = 1, int offsetY = 0) =>
        new(".300 BLK", 167.5 * scale, 55 * scale + offsetY)
        {
            Words = [new OcrWordBox(".300", 120 * scale, 46 * scale + offsetY, 49 * scale, 18 * scale),
                new OcrWordBox("BLK", 175 * scale, 46 * scale + offsetY, 40 * scale, 18 * scale)],
        };

    private static CapturedFrame Screenshot()
    {
        // 按用户 240×111 截图测量的颜色和几何合成,无需提交游戏截图。
        // 真实截图已在本地回放;词框仍为手工标注,不代替 Windows OCR 实机验证。
        var frame = new CapturedFrame(240, 111, new byte[240 * 111 * 4]);
        Fill(frame, 0, 0, 240, 111, 42, 49, 57);
        Fill(frame, 26, 22, 71, 71, 50, 40, 31);
        Fill(frame, 39, 40, 43, 27, 85, 108, 98); // 图案遮挡背景
        Fill(frame, 58, 75, 33, 8, 190, 190, 190); // 数量角标
        Fill(frame, 12, 8, 228, 2, 200, 205, 210);
        Fill(frame, 12, 104, 228, 2, 200, 205, 210);
        Fill(frame, 12, 8, 2, 98, 200, 205, 210);
        return frame;
    }

    private static CapturedFrame Scale(CapturedFrame frame, double scale)
    {
        int w = (int)(frame.Width * scale), h = (int)(frame.Height * scale);
        var pixels = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Array.Copy(frame.Bgra, ((int)(y / scale) * frame.Width + (int)(x / scale)) * 4,
                    pixels, (y * w + x) * 4, 4);
        return new CapturedFrame(w, h, pixels);
    }

    private static CapturedFrame StackRows(CapturedFrame top, CapturedFrame bottom) =>
        new(top.Width, top.Height + bottom.Height, [.. top.Bgra, .. bottom.Bgra]);

    private static void RemoveBorder(CapturedFrame frame)
    {
        Fill(frame, 10, 5, 230, 7, 42, 49, 57);
        Fill(frame, 10, 101, 230, 7, 42, 49, 57);
        Fill(frame, 10, 5, 7, 103, 42, 49, 57);
    }

    private static void Fill(CapturedFrame frame, int left, int top, int width, int height, int r, int g, int b)
    {
        for (int y = top; y < top + height; y++)
            for (int x = left; x < left + width; x++)
            {
                int p = (y * frame.Width + x) * 4;
                frame.Bgra[p] = (byte)b;
                frame.Bgra[p + 1] = (byte)g;
                frame.Bgra[p + 2] = (byte)r;
            }
    }
}
