using DeltaCrafter.Core.L2;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ReplenishPaymentTests
{
    [Fact]
    public void Initial_purchase_without_result_times_out()
    {
        var wait = new ReplenishPaymentWait(1000);
        Assert.False(wait.HasTimedOut(15999));
        Assert.True(wait.HasTimedOut(16000));
    }

    [Fact]
    public void Price_change_near_deadline_gets_a_fresh_result_window()
    {
        var wait = new ReplenishPaymentWait(0);
        Assert.True(wait.CanConfirmPriceChange(14000));
        wait.ConfirmPriceChange(14000);
        Assert.False(wait.HasTimedOut(16000));
        Assert.False(wait.HasTimedOut(28999));
        Assert.True(wait.HasTimedOut(29000));
    }

    [Fact]
    public void Persistent_dialog_is_not_clicked_again_until_response_interval_passes()
    {
        var wait = new ReplenishPaymentWait(0);
        Assert.False(wait.CanConfirmPriceChange(2999));
        Assert.True(wait.CanConfirmPriceChange(3000));
        wait.ConfirmPriceChange(3000);
        Assert.False(wait.CanConfirmPriceChange(5999));
        Assert.False(wait.OutcomeSettled(5999));
        Assert.True(wait.CanConfirmPriceChange(6000));
    }

    [Fact]
    public void Repeated_price_changes_continue_paying_but_stuck_ui_has_a_deadline()
    {
        var wait = new ReplenishPaymentWait(0);
        for (long now = 3000; now < 120000; now += 3000)
        {
            Assert.True(wait.CanConfirmPriceChange(now));
            wait.ConfirmPriceChange(now);
        }
        Assert.True(wait.HasTimedOut(120000));
        Assert.False(wait.CanConfirmPriceChange(120000));
    }
}
