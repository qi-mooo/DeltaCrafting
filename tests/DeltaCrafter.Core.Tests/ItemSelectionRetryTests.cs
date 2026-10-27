using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ItemSelectionRetryTests
{
    [Fact]
    public void Missing_evidence_or_selected_item_never_causes_extra_click()
    {
        var retry = new ItemSelectionRetry(0);
        Assert.False(retry.TryRetry(1999, true, true, false));
        Assert.False(retry.TryRetry(2000, false, true, false));
        Assert.False(retry.TryRetry(2000, true, false, false));
        Assert.False(retry.TryRetry(2000, true, true, true));
        Assert.True(retry.TryRetry(2000, true, true, false));
        Assert.False(retry.TryRetry(4000, true, true, false));
    }
}
