using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ProductionListOcrLayoutTests
{
    [Theory]
    [InlineData(1280, 720, 1)]
    [InlineData(1920, 1080, 1)]
    [InlineData(1920, 1080, 2)]
    [InlineData(2560, 1440, 2)]
    public void Production_icon_noise_is_masked_but_names_and_original_colors_survive(
        int width, int height, int upscale)
    {
        var list = new NRect { X = .046, Y = .178, W = .254, H = .742 };
        var pixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var original = pixels.ToArray();
        var frame = new CapturedFrame(width, height, pixels);
        var (x, y, w, h) = PixelMapper.ToPixelRect(list, width, height);
        int dw = w * upscale, dh = h * upscale;
        var masked = OcrImagePreprocessor.Prepare(frame, x, y, w, h, dw, dh,
            ProductionListOcrLayout.IconMasks(list));

        // 2026-09-29 08:48 失败转储中的图标文字/数量误读位置。
        Assert.Equal(0, At(130, 432)); // E (12%)
        Assert.Equal(0, At(130, 534)); // 品 (10%)
        Assert.Equal(0, At(133, 740)); // 52059 (57%)
        Assert.Equal(0, At(134, 842)); // fg (36%)
        Assert.Equal(0, At(156, 975)); // Rn (74%)，被裁切的数量 60。
        Assert.Equal(0, At(168, 365)); // 数量右缘。
        Assert.Equal(0, At(546, 318)); // 置顶箭头。

        Assert.Equal(255, At(190, 240)); // 9x39mm BP 首字。
        Assert.Equal(255, At(191, 342)); // .300 BLK 的小数点。
        Assert.Equal(255, At(396, 648)); // 较长名称末尾。
        Assert.Equal(255, At(526, 648)); // 名称列右端仍保留。
        Assert.Equal(original, pixels); // .300 品质与选中框验证必须读取原彩色帧。

        byte At(double sourceX, double sourceY)
        {
            int px = (int)((sourceX * width / 1920 - x) * dw / w);
            int py = (int)((sourceY * height / 1080 - y) * dh / h);
            return masked[(py * dw + px) * 4];
        }
    }
}
