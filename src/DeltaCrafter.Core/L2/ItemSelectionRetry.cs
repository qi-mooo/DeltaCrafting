namespace DeltaCrafter.Core.L2;

/// <summary>只在新截图仍明确显示空图纸区时补点一次，已选中或证据不足不重复点击。</summary>
internal sealed class ItemSelectionRetry(long clickedAt)
{
    private bool _used;
    internal bool TryRetry(long now, bool emptyPanel, bool uniqueTarget, bool selected)
    {
        if (_used || now - clickedAt < 2_000 || !emptyPanel || !uniqueTarget || selected) return false;
        _used = true;
        return true;
    }
}
