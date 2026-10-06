using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class SlotOcrLayoutTests
{
    [Theory]
    [InlineData(1280, 720, 1)]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 2)]
    [InlineData(2560, 1440, 2)]
    public void Only_fixed_icons_are_masked_and_source_colors_are_preserved(int width, int height, int upscale)
    {
        // 2026-09-29 07:25 失败截图：右上提示被读为方框/1，时钟被读为带圈数字。
        var slot = new NRect { X = .241, Y = .309, W = .178, H = .175 };
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var original = pixels.ToArray();
        var frame = new CapturedFrame(width, height, pixels);
        var (x, y, w, h) = PixelMapper.ToPixelRect(slot, width, height);
        int dw = w * upscale, dh = h * upscale;
        var masked = OcrImagePreprocessor.Prepare(frame, x, y, w, h, dw, dh, SlotOcrLayout.IconMasks(slot));

        Assert.Equal(0, At(.96, .06)); // 右上完成提示。
        Assert.Equal(0, At(.11, .80)); // 倒计时左侧时钟。
        Assert.Equal(255, At(.16, .13)); // 名称左端。
        Assert.Equal(255, At(.86, .13)); // 长名称右端。
        Assert.Equal(255, At(.185, .80)); // 倒计时首位，不得遮掉小时十位。
        Assert.Equal(255, At(.41, .80)); // 倒计时末位。
        Assert.Equal(255, At(.5, .63)); // 空闲状态文字。
        Assert.Equal(original, pixels); // 品质颜色仍使用原帧。

        byte At(double rx, double ry) => masked[((int)(ry * dh) * dw + (int)(rx * dw)) * 4];
    }

    [Fact]
    public void Generic_item_list_and_title_preprocessing_keeps_all_pixels()
    {
        var frame = new CapturedFrame(20, 20, Enumerable.Repeat((byte)255, 20 * 20 * 4).ToArray());
        var result = OcrImagePreprocessor.Prepare(frame, 0, 0, 20, 20, 20, 20);
        Assert.All(result, value => Assert.Equal(255, value));
    }

    [Fact]
    public void Masks_are_clipped_to_the_current_roi()
    {
        var frame = new CapturedFrame(20, 20, Enumerable.Repeat((byte)255, 20 * 20 * 4).ToArray());
        var result = OcrImagePreprocessor.Prepare(frame, 5, 5, 10, 10, 10, 10,
            [new NRect { X = 0, Y = 0, W = .1, H = .1 }]);
        Assert.All(result, value => Assert.Equal(255, value));
    }

    [Theory]
    [InlineData("高级护甲维修组合")]
    [InlineData("01:05:46")]
    [InlineData("01:05:□")]
    [InlineData(".300 BLK")]
    [InlineData("1")]
    public void Confidence_gate_does_not_discard_uncertain_text_or_digits(string text)
    {
        var result = OcrLineAssembler.Assemble([
            new OcrLine(text, 100, 60)
            {
                Confidence = .38f,
                Words = [new OcrWordBox(text, 40, 50, 120, 20)],
            },
        ]);
        Assert.True(result.HasUncertainText);
        Assert.Equal(text, result.FullText);
    }
}
