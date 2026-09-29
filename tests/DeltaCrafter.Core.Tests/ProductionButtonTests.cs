using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ProductionButtonTests
{
    [Theory]
    [InlineData("生产", true, false, false)]
    [InlineData("一键补齐", false, true, false)]
    [InlineData("补齐", false, true, false)]
    [InlineData("中止", false, false, true)]
    [InlineData("", false, false, false)]
    [InlineData("生广", false, false, false)]
    [InlineData("生产中", false, false, false)]
    [InlineData("停止生产", false, false, false)]
    [InlineData("生产一键补齐", false, false, false)]
    public void Actions_require_a_complete_unambiguous_button_label(string label,
        bool produce, bool replenish, bool abort)
    {
        var kw = new StateKeywords();
        Assert.Equal(produce, CraftStartFlow.LabelHits(label, kw.ButtonProduce));
        Assert.Equal(replenish, CraftStartFlow.LabelHits(label, kw.ButtonReplenish));
        Assert.Equal(abort, CraftStartFlow.LabelHits(label, kw.ButtonAbort));
    }
}
