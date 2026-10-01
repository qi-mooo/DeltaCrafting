namespace DeltaCrafter.Core.L1;

/// <summary>一行 OCR 结果。中心坐标为原始帧内的物理像素(已换算回裁剪/缩放前)。</summary>
public sealed record OcrLine(string Text, double CenterX, double CenterY)
{
    public float Confidence { get; init; } = 1;
    // 原始帧内的词框,仅供 .300 BLK 五级弹定位同一行左侧的品质图标。
    public IReadOnlyList<OcrWordBox> Words { get; init; } = [];
}

public sealed record OcrWordBox(string Text, double Left, double Top, double Width, double Height)
{
    public float? Confidence { get; init; }
}

public sealed record OcrReadout(string FullText, IReadOnlyList<OcrLine> Lines)
{
    public string? SourceText { get; init; }
    public bool HasUnreadableCountdown { get; init; }
    public const float MinimumItemConfidence = .90f;
    public bool HasUncertainText => HasTextBelowConfidence(MinimumItemConfidence);

    internal bool HasTextBelowConfidence(float minimumConfidence) =>
        Lines.Any(l => !float.IsFinite(l.Confidence) || l.Confidence < minimumConfidence);
}
