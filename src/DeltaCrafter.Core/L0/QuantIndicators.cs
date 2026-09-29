// Adapted from leitingquant/WindowSpy/QuantMath.cs (MIT).
// Copyright (c) 2026 Lei Ting Network Dev Studio. See licenses/leitingquant-LICENSE.txt.
namespace DeltaCrafter.Core.L0;

public sealed record QuantIndicatorResult(decimal Last, decimal Mean, decimal Ema,
    decimal Low, decimal High, double Rsi, decimal PositionPercent, double Volatility,
    decimal ChangePercent, int Samples);

public static class QuantIndicators
{
    public static QuantIndicatorResult? Calculate(IReadOnlyList<AmmoPricePoint> points, int period = 20)
    {
        if (period < 2) throw new ArgumentOutOfRangeException(nameof(period));
        if (points.Any(p => p.Price <= 0)) throw new ArgumentException("历史价格必须大于零。");
        if (points.Count < period) return null;
        var window = points.TakeLast(period).Select(p => p.Price).ToArray();
        decimal mean = window.Average(), low = window.Min(), high = window.Max(), last = window[^1];
        decimal ema = window[0], k = 2m / (period + 1);
        decimal gain = 0, loss = 0;
        for (int i = 1; i < window.Length; i++)
        {
            ema = window[i] * k + ema * (1 - k);
            decimal diff = window[i] - window[i - 1];
            if (diff >= 0) gain += diff; else loss -= diff;
        }
        double rsi = gain == 0 && loss == 0 ? 50 : loss == 0 ? 100 : 100 - 100 / (1 + (double)(gain / loss));
        double volatility = Math.Sqrt(window.Average(x => Math.Pow((double)(x - mean), 2)));
        return new(last, mean, ema, low, high, rsi, high == low ? 50 : (last - low) / (high - low) * 100,
            volatility, (last - window[0]) / window[0] * 100, period);
    }
}
