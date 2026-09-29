using System.Text.RegularExpressions;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>详情标题的「物品名 * 数量」只用名称匹配和检查置信度。</summary>
internal static class ItemTitleOcr
{
    private static readonly string[] WeaponSuffixes = ["步枪", "机枪", "冲锋枪", "霰弹枪", "散弹枪", "手枪", "狙击枪"];

    /// <summary>
    /// 武器详情标题的左边缘在游戏中比其它物品更靠左，旧 ROI 会裁掉首字。
    /// 坐标按 1920×1080 的实机标定向左扩展到 x=640；右边缘和垂直范围保持不变。
    /// </summary>
    internal static NRect AreaFor(string itemName, NRect configured)
    {
        string name = CatalogNameResolver.Canonical(itemName);
        bool weapon = name == "复合弓" ||
            WeaponSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal));
        if (!weapon) return configured;

        double left = Math.Min(configured.X, 640.0 / 1920.0);
        if (left == configured.X) return configured;
        double right = configured.X + configured.W;
        return new NRect { X = left, Y = configured.Y, W = right - left, H = configured.H };
    }

    internal static OcrReadout Normalize(OcrReadout readout)
    {
        var words = readout.Lines.SelectMany(l => l.Words.Select(w =>
            w with { Confidence = w.Confidence ?? l.Confidence })).OrderBy(w => w.Left).ToArray();
        if (words.Length == 0) return readout;
        var baseline = words.MaxBy(w => w.Height)!;
        // 只处理同一行标题；不能把不同高度的名称和倒计时拼成数量后缀。
        if (words.Any(w => Math.Abs(w.Top + w.Height / 2 - baseline.Top - baseline.Height / 2)
            > baseline.Height * .4)) return readout;
        string title = string.Join(" ", words.Select(w => w.Text));
        var match = Regex.Match(title, @"^(?<name>.+?)\s*[*＊×]\s*[1-9][0-9]*\s*$");
        if (!match.Success || !match.Groups["name"].Value.Any(char.IsLetter)) return readout;

        string name = match.Groups["name"].Value.Trim();
        // 分隔符和数量不是物品名，不让它们的低分污染名称；同一 OCR 区域内
        // 混有名称与数量时，无法拆分模型置信度，仍保留该区域的原始分数。
        int end = match.Groups["name"].Index + match.Groups["name"].Length, offset = 0;
        var evidence = new List<OcrWordBox>();
        foreach (var word in words)
        {
            if (offset >= end) break;
            string text = word.Text[..Math.Min(word.Text.Length, end - offset)].TrimEnd();
            if (text.Length > 0) evidence.Add(word with { Text = text });
            offset += word.Text.Length + 1;
        }
        var line = new OcrLine(name, evidence.Average(w => w.Left + w.Width / 2),
            evidence.Average(w => w.Top + w.Height / 2))
        { Words = evidence, Confidence = evidence.Min(w => w.Confidence!.Value) };
        return new(name, [line]);
    }
}
