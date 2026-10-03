using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>生产列表左侧的物品插画与数量不属于配方名称，仅从 OCR 副本中排除。</summary>
internal static class ProductionListOcrLayout
{
    internal static IReadOnlyList<NRect> IconMasks(NRect list) =>
    [
        // 1920×1080 实机：列表从 x=88 起，图标/数量到 x=170，名称从 x=190 起。
        // 随校准后的列表区域缩放，到 x≈176 为止，给名称首字及小数点留下余量。
        // 不按字符内容过滤：名称区域里的短型号、数字或低置信度文字仍参与严格校验。
        new() { X = list.X, Y = list.Y, W = list.W * .18, H = list.H },
        // 最右侧的置顶箭头也不是名称；名称列保留到 x≈535。
        new() { X = list.X + list.W * .915, Y = list.Y, W = list.W * .085, H = list.H },
    ];
}
