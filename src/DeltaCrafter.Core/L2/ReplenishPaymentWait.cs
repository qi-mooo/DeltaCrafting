namespace DeltaCrafter.Core.L2;

/// <summary>变价确认不限制金额；每次支付重新等待结果，并留出响应时间避免连点。</summary>
internal sealed class ReplenishPaymentWait
{
    private readonly long _startedAt;
    private long _lastPaymentAt;
    private long _deadline;

    public ReplenishPaymentWait(long startedAt)
    {
        _startedAt = startedAt;
        _lastPaymentAt = startedAt;
        _deadline = startedAt + 15_000;
    }

    // 限制无响应界面的等待时间，不限制材料价格或涨幅。
    public bool HasTimedOut(long now) => now >= _deadline || now - _startedAt >= 120_000;

    public bool OutcomeSettled(long now) => now - _lastPaymentAt >= 3_000;

    public bool CanConfirmPriceChange(long now) => !HasTimedOut(now) && OutcomeSettled(now);

    public void ConfirmPriceChange(long now)
    {
        _lastPaymentAt = now;
        _deadline = now + 15_000;
    }
}
