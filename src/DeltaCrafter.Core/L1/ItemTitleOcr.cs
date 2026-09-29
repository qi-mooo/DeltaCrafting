using System.Text.RegularExpressions;

namespace DeltaCrafter.Core.L1;

/// <summary>详情标题的「物品名 * 数量」只用名称匹配，保留名称和数量的置信度证据。</summary>
internal static class ItemTitleOcr
{
    internal static OcrReadout Normalize(OcrReadout readout)
    {
        var words = readout.Lines.SelectMany(l => l.Words).OrderBy(w => w.Left).ToArray();
        if (words.Length == 0) return readout;
        var baseline = words.MaxBy(w => w.Height)!;
        // 只处理同一行标题；不能把不同高度的名称和倒计时拼成数量后缀。
        if (words.Any(w => Math.Abs(w.Top + w.Height / 2 - baseline.Top - baseline.Height / 2)
            > baseline.Height * .4)) return readout;
        string title = string.Join(" ", words.Select(w => w.Text));
        var match = Regex.Match(title, @"^(?<name>.+?)\s*[*＊×]\s*[1-9][0-9]*\s*$");
        if (!match.Success || !match.Groups["name"].Value.Any(char.IsLetter)) return readout;

        string name = match.Groups["name"].Value.Trim();
        // 只豁免单独识别的数量分隔符。若它与名称合为低置信度区域，仍须停止。
        // Lines 保留数量和名称的原始读数/置信度，FullText 是用于严格匹配的物品名。
        var evidence = readout.Lines.Where(l => l.Text.Trim() is not ("*" or "＊" or "×")).ToArray();
        return new(name, evidence);
    }
}
