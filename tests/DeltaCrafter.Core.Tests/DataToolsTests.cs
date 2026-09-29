using System.Net;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class DataToolsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string Password = """{"code":0,"data":{"a":["0178","2026-09-29"],"b":["-","2026-09-29"]}}""";
    private const string Gun = """{"code":0,"count":13,"data":[{"id":7,"name":"M7方案","objectName":"M7战斗步枪","solutionCode":"M7战斗步枪-烽火地带-ABC123","solutionType":"gun","price":123456,"authorNickname":"","zb":999999}]}""";
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    [Theory]
    [InlineData("password", 6)]
    [InlineData("market", 2)]
    [InlineData("gun", 12)]
    public void Parses_recorded_provider_data(string tool, int count)
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"datatools-{tool}.json"));
        Assert.Equal(count, DataToolsClient.Parse(new(tool), json).Entries.Count);
    }

    [Theory]
    [InlineData("password", "map_pwd")]
    [InlineData("market", "market")]
    [InlineData("gun", "gun_gqm_v2")]
    public async Task Requests_only_the_opened_tool_and_does_not_prefetch(string tool, string endpoint)
    {
        int requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.EndsWith("/" + endpoint, request.RequestUri!.AbsolutePath);
            if (tool == "gun")
            {
                string query = Uri.UnescapeDataString(request.RequestUri.Query);
                Assert.Contains("key1=全部&key2=全部", query);
                Assert.Contains("solutionType=operator", query);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tool == "password" ? Password
                : tool == "gun" ? Gun : """{"code":0,"data":{"begin_date":"2026-09-25","end_date":"2026-10-02","items":[]}}""") });
        }));
        var client = new DataToolsClient(http);
        Assert.Equal(0, requests);
        await client.FetchAsync(new(tool, Mode: tool == "gun" ? "operator" : "gun"), Token, default);
        Assert.Equal(1, requests);
    }

    [Fact]
    public void Password_preserves_leading_zero_and_dates_and_marks_missing_maps()
    {
        var result = DataToolsClient.Parse(new("password"), Password);
        Assert.Equal(6, result.Entries.Count);
        Assert.Contains("0178", result.Entries[0].Detail);
        Assert.Contains("2026-09-29", result.Entries[0].Detail);
        Assert.Contains("暂未更新", result.Entries[1].Detail);
        Assert.Contains("暂未更新", result.Entries[5].Detail);
    }

    [Fact]
    public void Gun_copy_is_complete_and_last_page_does_not_use_short_page_length()
    {
        var first = DataToolsClient.Parse(new("gun"), Gun);
        Assert.True(first.HasNext);
        var last = DataToolsClient.Parse(new("gun", 2), Gun);
        Assert.False(last.HasNext);
        Assert.Equal("M7战斗步枪-烽火地带-ABC123", last.Entries[0].CopyText);
        Assert.DoesNotContain("999999", last.Entries[0].Detail); // Provider warns zb is inaccurate.
        Assert.Contains("未署名", last.Entries[0].Detail);
        Assert.Empty(DataToolsClient.Parse(new("gun"), """{"code":0,"count":0,"data":[]}""").Entries);
    }

    [Fact]
    public void Market_keeps_grade_date_range_and_optional_current_price()
    {
        var result = DataToolsClient.Parse(new("market"), """{"code":0,"data":{"begin_date":"2026-09-25","end_date":"2026-10-02","items":[{"id":1,"name":"腕带","grade":3,"price":29095}]}}""");
        Assert.Equal("3级 腕带", result.Entries[0].Title);
        Assert.Contains("2026-10-02", result.Entries[0].Detail);
        Assert.Equal("", result.Entries[0].CopyText);
    }

    [Fact]
    public async Task Errors_do_not_leak_tokens_or_automatically_retry()
    {
        int attempts = 0;
        using var http = new HttpClient(new Handler(_ => { attempts++; throw new HttpRequestException("token=" + Token); }));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new DataToolsClient(http).FetchAsync(new("gun"), Token, default));
        Assert.DoesNotContain(Token, ex.ToString());
        Assert.Equal(1, attempts);
        var provider = Assert.ThrowsAny<InvalidOperationException>(() => DataToolsClient.Parse(new("gun"), "{\"code\":403,\"msg\":\"" + Token + "\"}"));
        Assert.DoesNotContain(Token, provider.ToString());
    }

    [Theory]
    [InlineData("unknown", 1, "gun", "")]
    [InlineData("gun", 0, "gun", "")]
    [InlineData("gun", 1, "bad", "")]
    [InlineData("password", 2, "gun", "")]
    [InlineData("market", 1, "gun", "query")]
    [InlineData("gun", 1, "gun", "query\n")]
    public void Invalid_queries_are_rejected(string tool, int page, string mode, string search) =>
        Assert.False(new DataToolQuery(tool, page, mode, search).IsValid());
}
