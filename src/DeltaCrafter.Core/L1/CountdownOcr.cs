namespace DeltaCrafter.Core.L1;

/// <summary>倒计时只接受唯一的高置信度读数，并通过连续两帧确认。</summary>
internal sealed class CountdownOcr
{
    private TimeSpan? previous;
    private long previousAt;

    internal static bool TryRead(OcrReadout reading, out TimeSpan remaining)
    {
        remaining = default;
        var values = new HashSet<TimeSpan>();
        foreach (var line in OcrEvidence.Candidates(reading))
            if (OcrEvidence.IsConfident(line) && CountdownParser.TryParse(line.Text, out var time))
                values.Add(time);
        if (values.Count != 1) return false;
        remaining = values.Single();
        return true;
    }

    internal bool Observe(OcrReadout reading, long capturedAt, long now, out TimeSpan remaining)
    {
        remaining = default;
        if (!TryRead(reading, out var current)) return false;
        bool agreed = previous is { } old && capturedAt > previousAt &&
            Math.Abs((old - current).TotalMilliseconds - (capturedAt - previousAt)) <= 2000;
        previous = current;
        previousAt = capturedAt;
        if (!agreed) return false;
        remaining = current - TimeSpan.FromMilliseconds(Math.Max(0, now - capturedAt));
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        return true;
    }
}
