using System.Net;
using System.Text.Json.Nodes;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ManufactureDataTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static string Fixture(int type = 2) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"manufacture-{type}.json"));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    private static HttpResponseMessage Response(string? json = null) => new(HttpStatusCode.OK) { Content = new StringContent(json ?? Fixture()) };

    [Theory]
    [InlineData(1, 101)]
    [InlineData(2, 60)]
    [InlineData(3, 13)]
    [InlineData(4, 31)]
    public void Parses_recorded_four_facility_responses(int type, int count)
    {
        Assert.Equal(count, ManufactureApiClient.Parse(Fixture(type)).Count);
    }

    [Fact]
    public void Ammo_grades_are_distinct_and_keep_api_profit_values()
    {
        var rows = ManufactureApiClient.Parse(Fixture());
        foreach (int grade in new[] { 3, 4, 5 })
        {
            var item = rows.Single(i => i.Name == BlkAmmoIdentity.Name(grade));
            Assert.Equal(grade, item.Grade);
            Assert.Equal($"{grade}级 .300BLK", CatalogItemLabel.Format(item.ToCatalogItem()));
        }
        Assert.Equal(31440, rows.Single(i => i.Grade == 3 && BlkAmmoIdentity.Grade(i.Name) == 3).Profit);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"code\":0,\"data\":[]}")]
    [InlineData("{\"code\":0,\"data\":[{}]}")]
    [InlineData("{\"code\":\"0\",\"data\":[]}")]
    public void Malformed_data_is_rejected_without_exposing_response(string json) =>
        Assert.Throws<InvalidOperationException>(() => ManufactureApiClient.Parse(json));

    [Theory]
    [InlineData("grade", "5")]
    [InlineData("period", "0")]
    [InlineData("unlockLevel", "4")]
    [InlineData("objectID", "0")]
    [InlineData("price", "\"secret\"")]
    public void Invalid_rows_cannot_partially_replace_catalog(string field, string value)
    {
        var root = JsonNode.Parse(Fixture())!;
        root["data"]![0]![field] = JsonNode.Parse(value);
        Assert.Throws<InvalidOperationException>(() => ManufactureApiClient.Parse(root.ToJsonString()));
    }

    [Fact]
    public void Duplicate_identity_is_rejected()
    {
        var root = JsonNode.Parse(Fixture())!;
        root["data"]!.AsArray().Add(root["data"]![0]!.DeepClone());
        Assert.Throws<InvalidOperationException>(() => ManufactureApiClient.Parse(root.ToJsonString()));
    }

    [Theory]
    [InlineData(FacilityKey.TechCenter, 1)]
    [InlineData(FacilityKey.Workbench, 2)]
    [InlineData(FacilityKey.PharmacyLab, 3)]
    [InlineData(FacilityKey.ArmorStation, 4)]
    public async Task Requests_correct_facility_and_level(FacilityKey key, int type)
    {
        using var http = new HttpClient(new Handler((r, _) =>
        {
            Assert.Equal("https://orzice.com/workApi/v1/sjz_api/manufacturePro", r.RequestUri!.GetLeftPart(UriPartial.Path));
            Assert.Contains($"t={type}&l=2&token={Token}", r.RequestUri.Query);
            return Task.FromResult(Response(Fixture(type)));
        }));
        var result = await new ManufactureApiClient(http).FetchAsync(key, 2, Token, default);
        Assert.Equal(key, result.Facility);
        Assert.Equal(2, result.Level);
    }

    [Fact]
    public async Task Transport_and_server_errors_do_not_expose_credentials()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new HttpRequestException("URL?token=" + Token)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ManufactureApiClient(http).FetchAsync(FacilityKey.Workbench, 3, Token, default));
        Assert.DoesNotContain(Token, error.ToString());
        var denied = Assert.ThrowsAny<InvalidOperationException>(() => ManufactureApiClient.Parse("{\"code\":403,\"msg\":\"" + Token + "\"}"));
        Assert.DoesNotContain(Token, denied.ToString());
    }

    [Fact]
    public void Catalog_refresh_does_not_consume_or_require_quote_fields()
    {
        var root = JsonNode.Parse(Fixture())!;
        foreach (var row in root["data"]!.AsArray())
        {
            row!.AsObject().Remove("price");
            row.AsObject().Remove("price_hour");
        }
        var items = ManufactureApiClient.Parse(root.ToJsonString(), catalogOnly: true);
        Assert.Equal(60, items.Count);
        Assert.All(items, item => { Assert.Equal(0, item.Profit); Assert.Equal(0, item.HourlyProfit); });
        Assert.Equal(8, items.Single(i => i.Name == ".300BLK五级弹").PeriodHours);
        Assert.Throws<InvalidOperationException>(() => ManufactureApiClient.Parse(root.ToJsonString()));
    }

    [Fact]
    public void Recommendations_respect_level_and_profit_mode()
    {
        var snapshot = new ManufactureMarketSnapshot(FacilityKey.Workbench, 1, DateTimeOffset.Now,
        [new(1, "总利润", 3, 1, 10, 100, 10), new(2, "小时利润", 3, 1, 1, 80, 80), new(3, "未解锁", 5, 2, 1, 900, 900)]);
        Assert.Equal("总利润", snapshot.Best(CraftMode.TotalProfit).ItemName);
        Assert.Equal("小时利润", snapshot.Best(CraftMode.HourlyProfit).ItemName);
        Assert.Throws<InvalidOperationException>(() => snapshot.Best(CraftMode.Custom));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("8761")]
    [InlineData("null")]
    [InlineData("\"8h\"")]
    public void Catalog_refresh_rejects_invalid_duration(string value)
    {
        var root = JsonNode.Parse(Fixture())!;
        root["data"]![0]!["period"] = JsonNode.Parse(value);
        Assert.Throws<InvalidOperationException>(() => ManufactureApiClient.Parse(root.ToJsonString(), catalogOnly: true));
    }

    [Fact]
    public void Catalog_preserves_fractional_hours_and_queried_facility_level()
    {
        var root = JsonNode.Parse(Fixture())!;
        root["data"]![0]!["period"] = 4.5;
        var items = ManufactureApiClient.Parse(root.ToJsonString(), catalogOnly: true);
        var snapshots = FacilityKeys.All.Select(key => new ManufactureMarketSnapshot(key, 2, DateTimeOffset.Now, items)).ToArray();
        var catalog = ManufactureCatalog.Build(snapshots, new ItemCatalog());
        var item = catalog.For(FacilityKey.Workbench)[0];
        Assert.Equal(4.5, item.PeriodHours);
        Assert.Equal(2, item.PeriodFacilityLevel);
        Assert.Equal(16200, ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.Workbench), item.Name, 2));
    }

    [Fact]
    public void Catalog_requires_all_facilities_and_preserves_distinct_names_without_legacy_ammo_aliases()
    {
        var snapshots = FacilityKeys.All.Select(key => new ManufactureMarketSnapshot(key, 3, DateTimeOffset.Now,
            [new(1, ".300BLK三级弹", 3, 1, 5, 100, 20), new(2, "全景红点", 3, 1, 1, 10, 10)])).ToArray();
        var builtIn = new ItemCatalog { Revision = 7, Facilities = new()
        {
            ["workbench"] = [new() { Name = ".300BLK SUB-3" }, new() { Name = "侧置全景红点" }],
        }};
        Assert.Throws<InvalidOperationException>(() => ManufactureCatalog.Build(snapshots.Take(3).ToArray(), builtIn));
        var result = ManufactureCatalog.Build(snapshots, builtIn);
        Assert.Equal(7, result.Revision);
        Assert.Equal(new[] { ".300BLK三级弹", "全景红点", "侧置全景红点" }, result.For(FacilityKey.Workbench).Select(i => i.Name));
        Assert.Equal(".300BLK SUB-3", builtIn.For(FacilityKey.Workbench)[0].Name);
    }
}
