using System.Security.Cryptography;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>
/// 仅处理目录里的 .300BLK五级弹。游戏文字只有 .300 BLK,必须同时确认左侧金色图标。
/// 颜色依据用户提供的 240×111 行截图:暗金底色约 RGB(50,40,31),不是亮黄色。
/// 不修改全局 OCR 容差,不将此颜色规则推广到其他物品或其他等级。
/// </summary>
internal static class BlkAmmoMatcher
{
    internal const string GameName = ".300 BLK";

    internal sealed record Candidate(double X, double Y, double NameRight, PixelRect Icon);

    internal static bool AppliesTo(FacilityKey key, string displayName, string searchName) =>
        key == FacilityKey.Workbench && (IsLevelFive(displayName) || IsLevelFive(searchName));

    private static bool IsLevelFive(string name) =>
        TextMatch.Canonical(name.ToUpperInvariant()) is "300BLK五级弹" or "300BLK5级弹";

    internal static Candidate? Find(CapturedFrame frame, IReadOnlyList<OcrLine> lines, NRect listArea,
        bool requireSelected = false)
    {
        Candidate? found = null;
        foreach (var line in lines)
        {
            if (!TextMatch.LineContains(line.Text, GameName)) continue;
            var name = FindNameBox(line.Words);
            if (name is null) continue; // 缺少词框时不能确定颜色归属,不猜坐标。
            var icon = FindGoldIcon(frame, name, listArea);
            if (icon is null) continue;
            var candidate = new Candidate(name.Left + name.Width / 2, name.Top + name.Height / 2,
                name.Left + name.Width, icon);
            if (requireSelected && !HasSelectionBorder(frame, candidate)) continue;
            if (found is not null && found.Icon != icon) return null; // 两个候选时拒绝猜测。
            found = candidate;
        }
        return found;
    }

    private static OcrWordBox? FindNameBox(IReadOnlyList<OcrWordBox> words)
    {
        // 数量角标可能被 OCR 并入同一行,只取组成名称的词,避免把「120」当成图标右边界。
        for (int start = 0; start < words.Count; start++)
        {
            string text = "";
            double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
            for (int end = start; end < words.Count; end++)
            {
                var word = words[end];
                text += TextMatch.Canonical(word.Text);
                if (text.Length > 6) break;
                left = Math.Min(left, word.Left);
                top = Math.Min(top, word.Top);
                right = Math.Max(right, word.Left + word.Width);
                bottom = Math.Max(bottom, word.Top + word.Height);
                if (text == "300BLK" && right > left && bottom > top)
                    return new OcrWordBox(GameName, left, top, right - left, bottom - top);
            }
        }
        return null;
    }

    private static PixelRect? FindGoldIcon(CapturedFrame frame, OcrWordBox name, NRect listArea)
    {
        var (lx, ly, lw, lh) = PixelMapper.ToPixelRect(listArea, frame.Width, frame.Height);
        double cy = name.Top + name.Height / 2;
        // 截图中图标边长约为名称宽度的 0.75 倍。搜索同一行左侧,按文字大小缩放。
        int x0 = Math.Max(lx, (int)Math.Floor(name.Left - name.Width * 1.3));
        int x1 = Math.Min(lx + lw, (int)Math.Ceiling(name.Left));
        int y0 = Math.Max(ly, (int)Math.Floor(cy - name.Width * .65));
        int y1 = Math.Min(ly + lh, (int)Math.Ceiling(cy + name.Width * .65));
        int width = x1 - x0, height = y1 - y0;
        if (width <= 0 || height <= 0) return null;

        var gold = new bool[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int p = ((y0 + y) * frame.Width + x0 + x) * 4;
                gold[y * width + x] = IsGold(frame.Bgra[p + 2], frame.Bgra[p + 1], frame.Bgra[p]);
            }

        PixelRect? found = null;
        var queue = new Queue<int>();
        for (int i = 0; i < gold.Length; i++)
        {
            if (!gold[i]) continue;
            gold[i] = false;
            queue.Enqueue(i);
            int minX = width, minY = height, maxX = 0, maxY = 0, count = 0;
            while (queue.TryDequeue(out int p))
            {
                int x = p % width, y = p / width;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                count++;
                if (x > 0) Visit(p - 1);
                if (x + 1 < width) Visit(p + 1);
                if (y > 0) Visit(p - width);
                if (y + 1 < height) Visit(p + width);
            }
            int w = maxX - minX + 1, h = maxY - minY + 1;
            // 要求大块、近似方形的连续底色,拒绝弹药图案、金色文字或相邻行的图标。
            if (w < name.Width * .45 || w > name.Width * 1.05 ||
                h < name.Width * .45 || h > name.Width * 1.05 ||
                (double)w / h is < .75 or > 1.33 || count < w * h * .45 ||
                Math.Abs(y0 + (minY + maxY) / 2.0 - cy) > h * .20 ||
                minX == 0 || minY == 0 || maxX == width - 1 || maxY == height - 1)
                continue; // 被裁切的图标无法完整验证。
            var icon = new PixelRect(x0 + minX, y0 + minY, w, h);
            if (found is not null) return null;
            found = icon;
        }
        return found;

        void Visit(int p)
        {
            if (!gold[p]) return;
            gold[p] = false;
            queue.Enqueue(p);
        }
    }

    private static bool IsGold(byte r, byte g, byte b)
    {
        // 金色的明暗会变化;用色相/饱和度和通道差识别,包含截图里的暗棕金背景。
        if (r < 26 || r > 217 || r - g < 5 || g - b < 3) return false;
        double saturation = (double)(r - b) / r;
        double hue = 60.0 * (g - b) / (r - b); // R 为最大、B 为最小。
        return saturation is >= .18 and <= .85 && hue is >= 18 and <= 52;
    }

    private static bool HasSelectionBorder(CapturedFrame frame, Candidate candidate)
    {
        // 同名标题无法证明点中了哪一等级,因此还要确认金色行外围的白色选中框。
        // 依据同一截图的框距(约图标边长的 0.18 倍),允许缩放和 JPEG 边缘误差。
        var icon = candidate.Icon;
        int near = Math.Max(2, (int)(icon.Height * .08));
        int far = Math.Max(near + 1, (int)(icon.Height * .30));
        int right = Math.Min(frame.Width - 1, (int)candidate.NameRight);
        int top = -1, bottom = -1;
        for (int d = near; d <= far; d++)
        {
            if (Horizontal(icon.Top - d)) top = icon.Top - d;
            if (Horizontal(icon.Top + icon.Height - 1 + d)) bottom = icon.Top + icon.Height - 1 + d;
        }
        if (top < 0 || bottom < 0) return false;
        for (int d = near; d <= far; d++)
        {
            int x = icon.Left - d;
            if (x < 0) continue;
            int white = 0;
            for (int y = top; y <= bottom; y++) if (IsWhite(x, y)) white++;
            if (white >= (bottom - top + 1) * .85) return true;
        }
        return false;

        bool Horizontal(int y)
        {
            if (y < 0 || y >= frame.Height || right <= icon.Left) return false;
            int white = 0;
            for (int x = icon.Left; x <= right; x++) if (IsWhite(x, y)) white++;
            return white >= (right - icon.Left + 1) * .85;
        }

        bool IsWhite(int x, int y)
        {
            int p = (y * frame.Width + x) * 4;
            int b = frame.Bgra[p], g = frame.Bgra[p + 1], r = frame.Bgra[p + 2];
            return Math.Min(r, Math.Min(g, b)) >= 145 &&
                Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) <= 45;
        }
    }

    internal static string ViewSignature(CapturedFrame frame, NRect area)
    {
        // 同名不同等级滚入视野时 OCR 文本可能完全不变,不能沿用纯文本的「见底」判断。
        var (x, y, w, h) = PixelMapper.ToPixelRect(area, frame.Width, frame.Height);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int row = y; row < y + h; row++)
            hash.AppendData(frame.Bgra, (row * frame.Width + x) * 4, w * 4);
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
