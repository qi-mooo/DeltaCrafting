namespace DeltaCrafter.Core.L2;

/// <summary>未知画面的 ESC 兜底按连续等待时间限流,不按 OCR 轮询次数触发。</summary>
internal sealed class LaunchEscapeFallback
{
    private long? _unknownSince;
    public int Attempts { get; private set; }

    // 仅判断资格;调用方再次截帧确认仍未知后才发送按键并记录次数。
    public bool Observe(string? screen, long observedAt)
    {
        if (screen is not null)
        {
            _unknownSince = null;
            return false;
        }

        _unknownSince ??= observedAt;
        return Attempts < 3 && observedAt - _unknownSince.Value >= 60_000;
    }

    public void RecordEscape(long sentAt)
    {
        Attempts++;
        _unknownSince = sentAt;
    }
}
