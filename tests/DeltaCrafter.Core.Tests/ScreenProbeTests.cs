using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ScreenProbeTests
{
    private static AnchorTable Anchors => new JsonStoreBrick().Load<AnchorTable>(
        Path.Combine(AppContext.BaseDirectory, "Data", "anchors.json"));

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Item_title_excludes_quality_icon_toolbar_and_price_row(int width, int height)
    {
        var (x, y, w, h) = PixelMapper.ToPixelRect(
            Anchors.Screen(AnchorKeys.Production).Roi(AnchorKeys.RoiDetailTitle), width, height);
        double scale = width / 1920.0;
        Assert.InRange(x / scale, 665, 667);
        Assert.InRange((x + w) / scale, 907, 909);
        Assert.True(y / scale <= 168 && (y + h) / scale >= 195); // 覆盖完整标题。
        Assert.True((y + h) / scale < 212); // 价格行从 y=212 开始。
        Assert.True(Anchors.Revision > 10);
    }

    [Fact]
    public void Current_mode_accepts_observed_windows_ocr_for_first_play_card()
    {
        // 2026-09-28 实机截图裁剪,Windows zh-Hans OCR 1.5x 的真实输出。
        Assert.True(ScreenProbe.MatchesScreenTexts(Anchors.Screen(AnchorKeys.ModeSelectPlay),
            ["前 往 游 玩 ”", "烽 火 地 带"]));
    }

    [Theory]
    [InlineData("前往游玩", "全面战场")]
    [InlineData("前往游玩", "黑潮爆破")]
    [InlineData("前往游玩", "红鼠窝竞技场")]
    [InlineData("前往游玩", "")]
    [InlineData("", "烽火地带")]
    public void Play_button_alone_or_other_mode_cannot_trigger_entry(string button, string mode)
    {
        Assert.False(ScreenProbe.MatchesScreenTexts(Anchors.Screen(AnchorKeys.ModeSelectPlay), [button, mode]));
    }

    [Fact]
    public void Missing_or_swapped_probe_results_are_rejected()
    {
        var spec = Anchors.Screen(AnchorKeys.ModeSelectPlay);
        Assert.False(ScreenProbe.MatchesScreenTexts(spec, ["前往游玩"]));
        Assert.False(ScreenProbe.MatchesScreenTexts(spec, ["烽火地带", "前往游玩"]));
    }

    [Fact]
    public void Legacy_mode_and_other_single_probe_screens_remain_supported()
    {
        Assert.True(ScreenProbe.MatchesScreenTexts(Anchors.Screen(AnchorKeys.ModeSelect), ["战 役 模 式"]));
        Assert.True(ScreenProbe.MatchesScreenTexts(Anchors.Screen(AnchorKeys.Lobby), ["行 前 备 战"]));
        Assert.False(ScreenProbe.MatchesScreenTexts(new ScreenSpec(), [""]));
    }

    [Fact]
    public void Exit_menu_requires_both_exit_label_and_return_hint()
    {
        var spec = Anchors.Screen(AnchorKeys.ModeExitMenu);
        Assert.True(ScreenProbe.MatchesScreenTexts(spec, ["退 出 游 戏", "返 回"]));
        Assert.False(ScreenProbe.MatchesScreenTexts(spec, ["退出游戏", ""]));
        Assert.False(ScreenProbe.MatchesScreenTexts(spec, ["", "返回"]));
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void New_entry_point_stays_inside_first_card_play_strip(int width, int height)
    {
        var spec = Anchors.Screen(AnchorKeys.ModeSelectPlay);
        var (x, y) = PixelMapper.ToPixel(spec.Point(AnchorKeys.PointModeEntry), 0, 0, width, height);
        // 实机 1920×1080 的第一张卡绿色按钮边界 x=90..604, y=936..996。
        Assert.InRange(x, (int)(90.0 / 1920 * width), (int)(604.0 / 1920 * width));
        Assert.InRange(y, (int)(936.0 / 1080 * height), (int)(996.0 / 1080 * height));
        Assert.True(Anchors.Revision > 9); // 已安装用户必须获得备份后的默认锚点升级。
    }
}
