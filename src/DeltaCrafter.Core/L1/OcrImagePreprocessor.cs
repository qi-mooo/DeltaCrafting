using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>OCR 专用灰阶副本；保留原帧及坐标，不影响品质颜色检测。</summary>
internal static class OcrImagePreprocessor
{
    internal static byte[] Prepare(CapturedFrame frame, int cx, int cy, int cw, int ch, int dw, int dh,
        IReadOnlyList<NRect>? iconMasks = null)
    {
        if (cw <= 0 || ch <= 0 || dw <= 0 || dh <= 0 || cx < 0 || cy < 0 ||
            cx + cw > frame.Width || cy + ch > frame.Height)
            throw new ArgumentOutOfRangeException(nameof(cw));

        var gray = new byte[cw * ch];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < cw; x++)
            {
                int p = ((cy + y) * frame.Width + cx + x) * 4;
                gray[y * cw + x] = (byte)((frame.Bgra[p + 2] * 299 +
                    frame.Bgra[p + 1] * 587 + frame.Bgra[p] * 114 + 500) / 1000);
            }

        // 在 OCR 副本里遮掉固定图标，避免它们与文字合并后拉低整行置信度。
        // 原始彩色帧仍用于 .300 BLK 的品质判断及诊断截图。
        foreach (var mask in iconMasks ?? [])
        {
            var (mx, my, mw, mh) = PixelMapper.ToPixelRect(mask, frame.Width, frame.Height);
            int left = Math.Clamp(mx - cx, 0, cw), right = Math.Clamp(mx + mw - cx, 0, cw);
            int top = Math.Clamp(my - cy, 0, ch), bottom = Math.Clamp(my + mh - cy, 0, ch);
            for (int y = top; y < bottom; y++)
                gray.AsSpan(y * cw + left, right - left).Clear();
        }

        // 三次插值保留细笔画的过渡；最近邻在实机上会把「甲」读作「印」。
        var horizontal = new byte[dw * ch];
        for (int y = 0; y < ch; y++)
            for (int x = 0; x < dw; x++)
                horizontal[y * dw + x] = Sample(gray, y * cw, 1, cw, (x + .5) * cw / dw - .5);
        var resized = new byte[dw * dh];
        int low = 255, high = 0;
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                byte value = Sample(horizontal, x, dw, ch, (y + .5) * ch / dh - .5);
                resized[y * dw + x] = value;
                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }

        // 不裁掉亮端百分位：细笔画只占很少像素，过度拉伸会丢失细节。
        // 动画中的近单色区域保持灰阶，避免将轻微压缩噪声放大成文字。
        double gain = high - low >= 32 ? Math.Min(2.5, 255.0 / (high - low)) : 1;
        int offset = gain > 1 ? low : 0;
        var result = new byte[dw * dh * 4];
        for (int i = 0; i < resized.Length; i++)
        {
            byte value = (byte)Math.Clamp((int)((resized[i] - offset) * gain), 0, 255);
            result[i * 4] = result[i * 4 + 1] = result[i * 4 + 2] = value;
            result[i * 4 + 3] = 255;
        }
        return result;
    }

    private static byte Sample(byte[] source, int start, int stride, int length, double position)
    {
        int center = (int)Math.Floor(position);
        double sum = 0, weights = 0;
        for (int i = center - 1; i <= center + 2; i++)
        {
            if (i < 0 || i >= length) continue;
            double distance = Math.Abs(position - i);
            double weight = distance <= 1
                ? ((1.5 * distance - 2.5) * distance) * distance + 1
                : ((-.5 * distance + 2.5) * distance - 4) * distance + 2;
            sum += source[start + i * stride] * weight;
            weights += weight;
        }
        return (byte)Math.Clamp((int)Math.Floor(sum / weights + .5), 0, 255);
    }
}
