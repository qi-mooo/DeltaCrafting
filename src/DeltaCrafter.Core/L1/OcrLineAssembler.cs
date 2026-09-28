namespace DeltaCrafter.Core.L1;

/// <summary>合并同一基线的相邻文字框，保留原帧坐标及最低置信度。</summary>
internal static class OcrLineAssembler
{
    internal static OcrReadout Assemble(IEnumerable<OcrLine> regions)
    {
        var lines = new List<OcrLine>();
        foreach (var region in regions.OrderBy(r => r.CenterX))
        {
            var word = region.Words.Single();
            int index = lines.FindIndex(line =>
            {
                var last = line.Words[^1];
                double height = Math.Min(last.Height, word.Height);
                double gap = word.Left - last.Left - last.Width;
                return Math.Abs(last.Top + last.Height / 2 - region.CenterY) <= height * .35
                    && gap >= -height * .15 && gap <= height * 1.5;
            });
            if (index < 0) { lines.Add(region); continue; }
            var previous = lines[index];
            var words = previous.Words.Append(word).ToArray();
            double left = words.Min(w => w.Left), right = words.Max(w => w.Left + w.Width);
            double top = words.Min(w => w.Top), bottom = words.Max(w => w.Top + w.Height);
            lines[index] = new OcrLine(previous.Text + " " + region.Text, (left + right) / 2, (top + bottom) / 2)
            {
                Words = words, Confidence = Math.Min(previous.Confidence, region.Confidence),
            };
        }
        var ordered = lines.OrderBy(l => l.CenterY).ThenBy(l => l.CenterX).ToArray();
        return new(string.Join("\n", ordered.Select(l => l.Text)), ordered);
    }
}
