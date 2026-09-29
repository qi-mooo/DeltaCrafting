using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>总览槽位中的固定图标区域，不参与文字识别。</summary>
internal static class SlotOcrLayout
{
    internal static IReadOnlyList<NRect> IconMasks(NRect slot) =>
    [
        // 右上完成提示位于名称右侧、上方；可被读成方框、括号或数字 1。
        new() { X = slot.X + slot.W * .91, Y = slot.Y, W = slot.W * .09, H = slot.H * .14 },
        // 左下时钟紧邻倒计时，图标最右缘与时间文字之间保留空隙。
        new() { X = slot.X, Y = slot.Y + slot.H * .68, W = slot.W * .165, H = slot.H * .32 },
    ];
}
