using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>先筛选匹配度严格大于 60% 的目标，再由调用方检查 OCR 置信度。
/// 近似匹配只决定是否检查，绝不把读数改写成目录名称或授权点击。</summary>
internal static class OcrMatchFilter
{
    internal const double MinimumMatchPercent = 60;

    internal static string[] CatalogTargets(IReadOnlyList<CatalogItem> items) => items
        .Select(i => BlkAmmoIdentity.Grade(i.Name) is not null ? ".300 BLK" : i.Name)
        .Distinct().ToArray();

    internal static OcrReadout Filter(OcrReadout readout, IReadOnlyList<string> targets,
        bool includeCountdown = false, Func<OcrLine, bool>? inCountdownArea = null)
    {
        var kept = new List<OcrLine>();
        bool unreadableCountdown = false;
        foreach (var line in readout.Lines)
        {
            bool timerArea = includeCountdown && inCountdownArea?.Invoke(line) == true;
            double timerScore = includeCountdown ? CountdownMatchPercent(line.Text, timerArea) : 0;
            bool timer = timerScore > MinimumMatchPercent;
            bool name = targets.Any(t => MatchPercent(line.Text, t) > MinimumMatchPercent);
            if (name || timer) kept.Add(line);
            // 丢掉计时区域里的乱码，不检查它的置信度；但不能用这次缺失读数
            // 证明任务已完成。保留未读清标志，让槽位观察重试而不是误报可领取。
            if (includeCountdown && (timer || (timerArea && !name)) &&
                !CountdownParser.TryParse(line.Text, out _)) unreadableCountdown = true;
        }
        return new(string.Join("\n", kept.Select(l => l.Text)), kept)
        { SourceText = readout.SourceText ?? readout.FullText, HasUnreadableCountdown = unreadableCountdown };
    }

    internal static double MatchPercent(string observed, string expected) =>
        Similarity(CatalogNameResolver.Canonical(observed), CatalogNameResolver.Canonical(expected));

    // 全串编辑距离；短片段不会因为刚好是名称子串就得到 100%。
    private static double Similarity(string observed, string expected)
    {
        int length = Math.Max(observed.Length, expected.Length);
        if (observed.Length == 0 || expected.Length == 0) return 0;
        var previous = Enumerable.Range(0, expected.Length + 1).ToArray();
        var current = new int[expected.Length + 1];
        for (int i = 1; i <= observed.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= expected.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (observed[i - 1] == expected[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return 100.0 * (length - previous[expected.Length]) / length;
    }

    private static double CountdownMatchPercent(string text, bool timerArea)
    {
        if (CountdownParser.TryParse(text, out _)) return 100;
        if (!timerArea && !text.Any(c => c is ':' or '：' or '℃')) return 0;
        string shape = string.Concat(text.Replace("℃", ":0").Where(c => !char.IsWhiteSpace(c))
            .Select(c => char.IsDigit(c) || "OoОо口囗〇○lI|SsBZz".Contains(c)
                ? '#' : c is '：' or '，' ? ':' : c));
        return new[] { "#:##:##", "##:##:##", "###:##:##" }
            .Max(pattern => Similarity(shape, pattern));
    }
}
