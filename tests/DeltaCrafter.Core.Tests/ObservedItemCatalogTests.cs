using DeltaCrafter.Core.L0;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ObservedItemCatalogTests
{
    [Fact]
    public void Verified_slot_name_can_resolve_without_changing_manufacturable_items()
    {
        var original = new List<CatalogItem> { new() { Name = "7.62x51mm M61" } };
        var observed = ObservedItemCatalog.For(FacilityKey.Workbench, original);
        Assert.Equal("7.62x51mm LS", CatalogNameResolver.Resolve(observed, "7.62x51mm LS"));
        Assert.Null(CatalogNameResolver.Resolve(observed, "7.62x51mm L5"));
        Assert.Single(original);
        Assert.Same(original, ObservedItemCatalog.For(FacilityKey.ArmorStation, original));
    }

    [Fact]
    public void Api_item_takes_precedence_without_duplicate_names()
    {
        CatalogItem[] catalog = [new() { Name = "7.62×51mm LS" }];
        Assert.Same(catalog, ObservedItemCatalog.For(FacilityKey.Workbench, catalog));
    }
}
