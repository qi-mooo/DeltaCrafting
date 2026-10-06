namespace DeltaCrafter.Core.L1;

/// <summary>保留原始文字框置信度，避免相邻装饰图标拖低目标文字的整行置信度。</summary>
internal static class OcrEvidence
{
    internal static IEnumerable<OcrLine> Candidates(OcrReadout reading)
    {
        foreach (var line in reading.Lines)
        {
            yield return line;
            foreach (var word in line.Words)
                yield return new OcrLine(word.Text, word.Left + word.Width / 2, word.Top + word.Height / 2)
                { Confidence = word.Confidence ?? line.Confidence, Words = [word] };
        }
    }

    internal static bool IsConfident(OcrLine line) =>
        float.IsFinite(line.Confidence) && line.Confidence >= OcrReadout.MinimumItemConfidence;

    internal static OcrLine? FindTarget(OcrReadout reading, string target)
    {
        string expected = Compact(target);
        if (expected.Length == 0) return null;
        return Candidates(reading).FirstOrDefault(line => IsConfident(line) &&
            Compact(line.Text).Contains(expected, StringComparison.Ordinal));
    }

    private static string Compact(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
