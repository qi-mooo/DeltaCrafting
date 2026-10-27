namespace DeltaCrafter.Core.L0;

/// <summary>已由实机截图核实、制造接口可能缺失的在制/待领取名称。只用于观察，不加入制造计划。</summary>
internal static class ObservedItemCatalog
{
    internal static IReadOnlyList<CatalogItem> For(FacilityKey key, IReadOnlyList<CatalogItem> catalog)
    {
        // 183 2026-10-10 工作台待领取卡片的完整名称。不猜 ID、等级或制造时长。
        const string name = "7.62x51mm LS";
        if (key != FacilityKey.Workbench || catalog.Any(i =>
            CatalogNameResolver.Canonical(i.Name) == CatalogNameResolver.Canonical(name))) return catalog;
        return [.. catalog, new CatalogItem { Name = name }];
    }
}
