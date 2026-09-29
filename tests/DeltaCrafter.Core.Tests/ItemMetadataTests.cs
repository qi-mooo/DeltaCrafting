using System.Net;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ItemMetadataTests
{
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "item-metadata.json"));
    private static ItemCatalog Catalog(params CatalogItem[] items) => new()
        { Facilities = new() { ["workbench"] = items.ToList() } };
    private static ItemMetadataSnapshot Snapshot(params ItemMetadata[] items) => new(DateTimeOffset.Now, items);

    [Fact]
    public void Recorded_api_fills_armor_and_ammo_grades_including_level_seven()
    {
        var rows = ItemMetadataApiClient.Parse(Fixture);
        Assert.Equal(22, rows.Count);
        Assert.Equal(6, rows.Single(i => i.Name == "特里克MAS2.0装甲").Grade);
        Assert.Equal(4, rows.Single(i => i.Name == "5.8x42mm DBP10+P").Grade);
        Assert.Equal(5, rows.Single(i => i.Name == "5.8x42mm DVC12+P").Grade);
        Assert.Equal(7, rows.Single(i => i.Name == ".50 BMG M903 SLAP").Grade);
        Assert.DoesNotContain(rows, i => i.ObjectId == 0);
    }

    [Fact]
    public void Refresh_merges_api_metadata_without_changing_recipe_names_or_builtin_data()
    {
        var store = new JsonStoreBrick();
        var builtIn = store.Load<ItemCatalog>(Path.Combine(AppContext.BaseDirectory, "Data", "items.json"));
        var metadata = store.Load<ItemMetadataSnapshot>(Path.Combine(AppContext.BaseDirectory, "Data", "item-metadata.json"));
        FacilityKey[] keys = [FacilityKey.TechCenter, FacilityKey.Workbench, FacilityKey.PharmacyLab, FacilityKey.ArmorStation];
        var snapshots = keys.Select((key, index) => new ManufactureMarketSnapshot(key, 3, DateTimeOffset.Now,
            ManufactureApiClient.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", $"manufacture-{index + 1}.json")), catalogOnly: true))).ToArray();
        var catalog = ManufactureCatalog.Build(snapshots, builtIn);
        var names = catalog.Facilities.Values.SelectMany(i => i).Select(i => i.Name).ToArray();
        Assert.True(ItemMetadataCatalog.Apply(catalog, metadata) > 0);
        Assert.Equal(names, catalog.Facilities.Values.SelectMany(i => i).Select(i => i.Name));
        Assert.Equal(226, names.Length);
        Assert.All(catalog.For(FacilityKey.Workbench), i => Assert.InRange(i.Grade, 1, 7));
        Assert.All(catalog.For(FacilityKey.ArmorStation), i => Assert.InRange(i.Grade, 1, 6));
        Assert.Equal(6, catalog.For(FacilityKey.ArmorStation).Single(i => i.Name == "特里克MAS2.0装甲").Grade);
        Assert.DoesNotContain(names, n => n is "7.62×39mm SUB-4" or "7.62×39mm SUB-5");
        Assert.All(builtIn.Facilities.Values.SelectMany(i => i), i => Assert.Equal(0, i.Grade));
        Assert.Equal(0, ItemMetadataCatalog.Apply(catalog, metadata));
    }

    [Fact]
    public void Api_values_override_known_grades_but_preserve_names_notes_and_unlock_levels()
    {
        var item = new CatalogItem { Name = "7.62×54R SNB", Grade = 4, UnlockLevel = 3, Note = "自定义备注" };
        var catalog = Catalog(item);
        Assert.Equal(1, ItemMetadataCatalog.Apply(catalog, Snapshot(new ItemMetadata(37180600001, "7.62x54R SNB", 6))));
        Assert.Equal(37180600001, item.ObjectId);
        Assert.Equal(6, item.Grade);
        Assert.Equal("7.62×54R SNB", item.Name);
        Assert.Equal("自定义备注", item.Note);
        Assert.Equal(3, item.UnlockLevel);
        Assert.Equal(0, ItemMetadataCatalog.Apply(catalog, Snapshot(new ItemMetadata(37180600001, "7.62x54R SNB", 0))));
        Assert.Equal(6, item.Grade);
    }

    [Fact]
    public void Partial_names_conflicting_ids_and_ambiguous_names_cannot_supply_a_grade()
    {
        var catalog = Catalog(new() { Name = "侧置全景红点" },
            new() { Name = "全景红点", ObjectId = 99 }, new() { Name = "重复名称" });
        var metadata = Snapshot(new(1, "全景红点", 3), new(2, "重复名称", 4), new(3, "重复名称", 5));
        Assert.Equal(0, ItemMetadataCatalog.Apply(catalog, metadata));
        Assert.All(catalog.For(FacilityKey.Workbench), i => Assert.Equal(0, i.Grade));
    }

    [Fact]
    public void Id_matching_supports_provider_names_but_cannot_change_blk_identity()
    {
        var item = new CatalogItem { Name = ".300BLK五级弹", ObjectId = 37280500001, Grade = 5 };
        Assert.Equal(0, ItemMetadataCatalog.Apply(Catalog(item), Snapshot(new ItemMetadata(item.ObjectId, ".300BLK_5", 4))));
        Assert.Equal(5, item.Grade);
        item.Grade = 0;
        Assert.Equal(1, ItemMetadataCatalog.Apply(Catalog(item), Snapshot(new ItemMetadata(item.ObjectId, ".300BLK_5", 5))));
        Assert.Equal(".300BLK五级弹", item.Name);
    }

    [Fact]
    public void Retired_ammo_cleanup_preserves_api_confirmed_recipes_and_saved_selection()
    {
        var catalog = Catalog(new() { Name = "7.62×39mm SUB-4" }, new() { Name = "7.62x39mm SUB-5" },
            new() { Name = "7.62×39mm SUB-4", ObjectId = 10 }, new() { Name = ".300BLK SUB-4" });
        var plan = new FacilityPlan { Key = FacilityKey.Workbench, ItemName = "7.62x39mm SUB-5" };
        Assert.Equal(2, ItemMetadataCatalog.RemoveRetiredLegacyAmmo(catalog));
        Assert.Equal(2, catalog.For(FacilityKey.Workbench).Count);
        var options = DeviceItemList.CreateWithMetadata(plan, catalog.For(FacilityKey.Workbench));
        Assert.Equal(plan.ItemName, options.SelectedItemName);
        Assert.Contains(plan.ItemName, options.Items);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"code\":0,\"data\":[]}")]
    [InlineData("{\"code\":0,\"data\":[{}]}")]
    [InlineData("{\"code\":0,\"data\":[{\"objectID\":1,\"objectName\":\"bad\",\"grade\":8}]}")]
    public void Malformed_metadata_is_rejected(string json) =>
        Assert.Throws<InvalidOperationException>(() => ItemMetadataApiClient.Parse(json));

    [Fact]
    public async Task Metadata_is_fetched_only_on_explicit_call_and_never_retried()
    {
        int count = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            count++;
            Assert.Equal("https://orzice.com/workApi/v1/sjz_api/item_info_all", request.RequestUri!.GetLeftPart(UriPartial.Path));
            return new(HttpStatusCode.OK) { Content = new StringContent(Fixture) };
        }));
        var client = new ItemMetadataApiClient(http);
        Assert.Equal(0, count);
        Assert.Equal(22, (await client.FetchAsync(new string('a', 32), default)).Items.Count);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Metadata_errors_do_not_expose_token_or_api_response()
    {
        string token = new('b', 32);
        using var http = new HttpClient(new Handler(_ => throw new HttpRequestException("URL?token=" + token)));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new ItemMetadataApiClient(http).FetchAsync(token, default));
        Assert.DoesNotContain(token, ex.ToString());
        var denied = Assert.ThrowsAny<InvalidOperationException>(() => ItemMetadataApiClient.Parse($"{{\"code\":403,\"msg\":\"{token}\"}}"));
        Assert.DoesNotContain(token, denied.ToString());
    }

    [Fact]
    public void Level_seven_reaches_device_list_without_changing_item_identity()
    {
        var plan = new FacilityPlan { Key = FacilityKey.Workbench, ItemName = ".50 BMG M903 SLAP" };
        var list = DeviceItemList.CreateWithMetadata(plan, [new() { Name = plan.ItemName, Grade = 7 }]);
        var option = Assert.Single(list.Options!);
        Assert.Equal(7, option.Grade);
        Assert.Equal("7级 .50 BMG M903 SLAP", option.Label);
        Assert.Equal(plan.ItemName, option.Name);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(send(request));
    }
}
