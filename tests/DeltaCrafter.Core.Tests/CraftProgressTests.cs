using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class CraftProgressTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T02:00:00+08:00");

    [Theory]
    [InlineData(4, 50)]
    [InlineData(8, 0)]
    [InlineData(9, 0)]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    public void Progress_uses_total_period_and_clamps_to_valid_range(int hoursLeft, double expected) =>
        Assert.Equal(expected, CraftProgress.Percent(FacilityPhase.Crafting, Now.AddHours(hoursLeft), 28800, Now));

    [Fact]
    public void Unknown_period_is_not_guessed_but_completion_still_displays_full()
    {
        Assert.Null(CraftProgress.Percent(FacilityPhase.Crafting, Now.AddHours(4), null, Now));
        Assert.Null(CraftProgress.Percent(FacilityPhase.Crafting, null, 28800, Now));
        Assert.Null(CraftProgress.Percent(FacilityPhase.Idle, Now, 28800, Now));
        Assert.Equal(100, CraftProgress.Percent(FacilityPhase.ReadyToCollect, null, null, Now));
        Assert.Equal(100, CraftProgress.Percent(FacilityPhase.Crafting, Now, null, Now));
    }

    [Theory]
    [InlineData(4.5, 16200L)]
    [InlineData(8.0, 28800L)]
    [InlineData(0.0, null)]
    [InlineData(-1.0, null)]
    [InlineData(8761.0, null)]
    [InlineData(double.NaN, null)]
    [InlineData(double.PositiveInfinity, null)]
    [InlineData(null, null)]
    public void Hours_require_finite_positive_duration(double? hours, long? expected) =>
        Assert.Equal(expected, CraftProgress.SecondsFromHours(hours));

    [Fact]
    public void Api_period_takes_priority_then_falls_back_to_confirmed_start()
    {
        var runtime = new FacilityRuntime { Phase = FacilityPhase.Crafting,
            StartedAt = Now.AddHours(-2), ReadyAt = Now.AddHours(4), RecipeTotalSeconds = 28800 };
        Assert.Equal(28800, CraftProgress.TotalSeconds(runtime));
        runtime.RecipeTotalSeconds = null;
        Assert.Equal(21600, CraftProgress.TotalSeconds(runtime));
        runtime.StartedAt = null;
        Assert.Null(CraftProgress.TotalSeconds(runtime));
        runtime.RecipeTotalSeconds = 28800;
        runtime.Phase = FacilityPhase.Idle;
        Assert.Null(CraftProgress.TotalSeconds(runtime));
    }

    [Fact]
    public void Bundled_periods_supply_pro_recipes_and_legacy_armor_supplements()
    {
        var store = new JsonStoreBrick();
        var catalog = store.Load<ItemCatalog>(Path.Combine(AppContext.BaseDirectory, "Data", "items.json"));
        var periods = store.Load<ItemCatalog>(Path.Combine(AppContext.BaseDirectory, "Data", "manufacture-periods.json"));
        Assert.True(ManufacturePeriods.Merge(catalog, periods, overwrite: false) > 0);
        Assert.Equal(43200, ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.TechCenter), "复合弓", 3));
        Assert.Equal(28800, ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.Workbench), ".300BLK五级弹", 3));
        Assert.Equal(28800, ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.PharmacyLab), "高级护甲维修组合", 3));
        Assert.Equal(43200, ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.ArmorStation), "特里克MAS2.0装甲", 3));
        Assert.Equal(43200, ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.ArmorStation), "DICH-9重型头盔", 3));
        Assert.Null(ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.ArmorStation), "特里克MAS2.0装甲", 2));
        Assert.Null(ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.TechCenter), "复合弓", 2));
    }

    [Fact]
    public void Period_lookup_requires_unique_full_identity_and_matching_facility_level()
    {
        CatalogItem[] rows = [new() { Name = ".300BLK_5", PeriodHours = 8, PeriodFacilityLevel = 3 },
            new() { Name = "侧置全景红点", PeriodHours = 4.5, PeriodFacilityLevel = 3 }];
        Assert.Equal(28800, ManufacturePeriods.SecondsFor(rows, ".300 BLK五级弹", 3));
        Assert.Null(ManufacturePeriods.SecondsFor(rows, ".300BLK四级弹", 3));
        Assert.Null(ManufacturePeriods.SecondsFor(rows, ".300BLK", 3));
        Assert.Null(ManufacturePeriods.SecondsFor(rows, "全景红点", 3));
        Assert.Null(ManufacturePeriods.SecondsFor(rows, ".300BLK五级弹", 2));
        Assert.Null(ManufacturePeriods.SecondsFor([.. rows, rows[0]], ".300BLK五级弹", 3));
    }

    [Fact]
    public void Merge_updates_duration_only_and_does_not_replace_newer_cache_with_seed()
    {
        var item = new CatalogItem { ObjectId = 42, Name = "本地名称", Grade = 5, Note = "保留", PeriodHours = 6, PeriodFacilityLevel = 2 };
        var catalog = new ItemCatalog { Facilities = new() { ["workbench"] = [item] } };
        var periods = new ItemCatalog { Facilities = new() { ["workbench"] = [
            new() { ObjectId = 42, Name = "接口名称", Grade = 4, PeriodHours = 8, PeriodFacilityLevel = 3 },
            new() { ObjectId = 43, Name = "未收录物品", PeriodHours = 8, PeriodFacilityLevel = 3 }] } };
        Assert.Equal(0, ManufacturePeriods.Merge(catalog, periods, overwrite: false));
        Assert.Equal(6, item.PeriodHours);
        Assert.Equal(1, ManufacturePeriods.Merge(catalog, periods, overwrite: true));
        Assert.Equal(8, item.PeriodHours);
        Assert.Equal(3, item.PeriodFacilityLevel);
        Assert.Equal("本地名称", item.Name);
        Assert.Equal(5, item.Grade);
        Assert.Equal("保留", item.Note);
        Assert.Single(catalog.For(FacilityKey.Workbench));
        Assert.Equal(0, ManufacturePeriods.Merge(catalog, periods, overwrite: true));
    }
}
