using System.Net;
using System.Text.Json.Nodes;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class ManufactureLegacyPeriodTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manufacture-legacy-armor.json"));
    private static ManufactureMarketSnapshot Pro => new(FacilityKey.ArmorStation, 3, DateTimeOffset.Now,
        ManufactureApiClient.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "manufacture-4.json"))));
    private static ItemCatalog Catalog => new() { Facilities = new() { ["armor-station"] = [
        new() { ObjectId = 11050006003, Name = "特里克MAS2.0装甲", Grade = 6 },
        new() { ObjectId = 11010005002, Name = "H09防暴头盔", PeriodHours = 8, PeriodFacilityLevel = 3 }] } };

    [Fact]
    public void Recorded_nontradable_armor_has_twelve_hour_period_without_profit_fields()
    {
        var root = JsonNode.Parse(Fixture)!;
        foreach (var row in root["data"]!.AsArray())
        {
            row!.AsObject().Remove("price");
            row.AsObject().Remove("price_hour");
        }
        var rows = ManufactureApiClient.ParseLegacyPeriods(root.ToJsonString());
        Assert.Equal(42, rows.Count);
        var mas = rows.Single(i => i.ObjectId == 11050006003);
        Assert.Equal("特里克MAS2.0装甲", mas.Name);
        Assert.Equal(12, mas.PeriodHours);
        var catalog = Catalog;
        Assert.Equal(1, ManufacturePeriods.SupplementFromLegacy(catalog, Pro, rows));
        Assert.Equal(43200, ManufacturePeriods.SecondsFor(catalog.For(FacilityKey.ArmorStation), mas.Name, 3));
        Assert.Equal(2, catalog.For(FacilityKey.ArmorStation).Count);
        Assert.Equal(6, catalog.For(FacilityKey.ArmorStation)[0].Grade);
        Assert.Equal(8, catalog.For(FacilityKey.ArmorStation)[1].PeriodHours);
    }

    [Fact]
    public void Legacy_cannot_override_pro_or_assume_other_levels_or_different_period_baselines()
    {
        var rows = ManufactureApiClient.ParseLegacyPeriods(Fixture);
        var catalog = Catalog;
        Assert.Equal(0, ManufacturePeriods.SupplementFromLegacy(catalog, Pro with { Level = 2 }, rows));
        Assert.Null(catalog.For(FacilityKey.ArmorStation)[0].PeriodHours);
        rows.Single(i => i.ObjectId == 11010005002).PeriodHours = 24;
        Assert.Equal(0, ManufacturePeriods.SupplementFromLegacy(catalog, Pro, rows));
        Assert.Null(catalog.For(FacilityKey.ArmorStation)[0].PeriodHours);
        Assert.Equal(8, catalog.For(FacilityKey.ArmorStation)[1].PeriodHours);
        rows.Single(i => i.ObjectId == 11010005002).PeriodHours = 8;
        catalog.For(FacilityKey.ArmorStation)[0].PeriodHours = 10;
        catalog.For(FacilityKey.ArmorStation)[0].PeriodFacilityLevel = 3;
        Assert.Equal(0, ManufacturePeriods.SupplementFromLegacy(catalog, Pro, rows));
        Assert.Equal(10, catalog.For(FacilityKey.ArmorStation)[0].PeriodHours);
    }

    [Theory]
    [InlineData("period", "0")]
    [InlineData("period", "8761")]
    [InlineData("period", "null")]
    [InlineData("objectID", "0")]
    [InlineData("name", "\"\"")]
    public void Invalid_period_response_is_rejected(string field, string value)
    {
        var root = JsonNode.Parse(Fixture)!;
        root["data"]![0]![field] = JsonNode.Parse(value);
        Assert.Throws<InvalidOperationException>(() => ManufactureApiClient.ParseLegacyPeriods(root.ToJsonString()));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }

    [Fact]
    public async Task Supplemental_fetch_uses_ordinary_endpoint_without_consuming_profit_or_exposing_token()
    {
        using var http = new HttpClient(new Handler(r =>
        {
            Assert.Equal("https://orzice.com/workApi/v1/sjz_api/manufacture", r.RequestUri!.GetLeftPart(UriPartial.Path));
            Assert.Equal($"?t=4&token={Token}", r.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(Fixture) };
        }));
        var rows = await new ManufactureApiClient(http).FetchLegacyPeriodsAsync(FacilityKey.ArmorStation, Token, default);
        Assert.Equal(12, rows.Single(i => i.ObjectId == 11050006003).PeriodHours);
        using var bad = new HttpClient(new Handler(_ => throw new HttpRequestException("URL?token=" + Token)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new ManufactureApiClient(bad)
            .FetchLegacyPeriodsAsync(FacilityKey.ArmorStation, Token, default));
        Assert.DoesNotContain(Token, error.ToString());
    }
}
