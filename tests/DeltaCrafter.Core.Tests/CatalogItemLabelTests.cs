using DeltaCrafter.Core.L0;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class CatalogItemLabelTests
{
    [Theory]
    [InlineData(".300BLK三级弹", 3, "3级 .300BLK")]
    [InlineData(".300BLK四级弹", 4, "4级 .300BLK")]
    [InlineData(".300BLK五级弹", 5, "5级 .300BLK")]
    [InlineData(".300BLK SUB-3", 0, "3级 .300BLK")]
    [InlineData("DICH-9重型头盔", 6, "6级 DICH-9重型头盔")]
    [InlineData("7.62x51mm M61", 6, "6级 7.62x51mm M61")]
    [InlineData(".50 BMG M903 SLAP", 7, "7级 .50 BMG M903 SLAP")]
    [InlineData("旧目录物品", 0, "等级未知 旧目录物品")]
    public void Grade_labels_keep_catalog_identity_and_match_key(string name, int grade, string expected)
    {
        var item = new CatalogItem { Name = name, Grade = grade };
        Assert.Equal(expected, CatalogItemLabel.Format(item));
        Assert.Equal(name, item.Name);
        Assert.Equal(name, item.MatchKey);
        Assert.Equal(name, CatalogItemLabel.ResolveSelection([item], expected));
        Assert.Equal(name, CatalogItemLabel.ResolveSelection([item], name));
    }

    [Fact]
    public void Similar_labels_cannot_change_the_selected_identity()
    {
        CatalogItem[] items = [new() { Name = ".300BLK三级弹", Grade = 3 },
            new() { Name = ".300BLK五级弹", Grade = 5 }];
        Assert.Equal(".300BLK五级弹", CatalogItemLabel.ResolveSelection(items, "5级 .300BLK"));
        Assert.Equal("4级 .300BLK", CatalogItemLabel.ResolveSelection(items, "4级 .300BLK"));
        Assert.Equal("", CatalogItemLabel.ForName(items, ""));
    }

    [Fact]
    public void Device_labels_preserve_original_selection_and_old_firmware_contract()
    {
        var plan = new FacilityPlan { Key = FacilityKey.Workbench, ItemName = ".300BLK五级弹" };
        CatalogItem[] items = [new() { Name = ".300BLK三级弹", Grade = 3 },
            new() { Name = ".300BLK五级弹", Grade = 5 }];
        var result = DeviceItemList.CreateWithMetadata(plan, items);
        Assert.Equal(new[] { ".300BLK三级弹", ".300BLK五级弹" }, result.Items);
        Assert.Equal(plan.ItemName, result.SelectedItemName);
        Assert.Equal(new[] { "3级 .300BLK", "5级 .300BLK" }, result.Options!.Select(i => i.Label));
        Assert.Equal(new[] { 3, 5 }, result.Options!.Select(i => i.Grade));
        Assert.Null(DeviceItemList.ValidateSelection(plan, result.Items, result.Options![1].Name));
        Assert.NotNull(DeviceItemList.ValidateSelection(plan, result.Items, result.Options![1].Label));
    }
}
