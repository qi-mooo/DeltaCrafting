using System.Net;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L3;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class AmmoQuantTests : IDisposable
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private const string Forecast = """{"code":0,"data":[{"hour":0,"data":[{"id":35,"oid":1222,"name":".300BLK_4","grade":4,"price":1687,"pic":"https://playerhub.df.qq.com/playerhub/60004/object/gun/ammo/300BLK.png"}]},{"hour":1,"data":[]},{"hour":17,"data":[{"id":35,"oid":1222,"name":".300BLK_4","grade":4,"price":1650,"pic":"https://orzice.com/update/sjz_pic/1222.png"}]}]}""";
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 15, 0, 0, TimeSpan.FromHours(8));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "delta-quant-test-" + Guid.NewGuid());
    private string FilePath => Path.Combine(_dir, "quant.json");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
    private sealed class Source : IAmmoMarketSource
    {
        public int Calls;
        public Func<CancellationToken, Task<AmmoForecast>> Fetch = _ => Task.FromResult(AmmoMarketClient.Parse(4, Forecast, Now));
        public Task<AmmoForecast> FetchLowPricesAsync(int grade, string token, CancellationToken ct) { Calls++; return Fetch(ct); }
        public Task<AmmoPriceHistory> FetchHistoryAsync(long objectId, string token, CancellationToken ct) =>
            Task.FromResult(new AmmoPriceHistory(objectId, Now, []));
    }

    [Fact]
    public void Keeps_hour_groups_and_ids_separate_from_display_labels()
    {
        var result = AmmoMarketClient.Parse(4, Forecast, Now);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(new DateOnly(2026, 9, 29), result.ForecastDate);
        Assert.Equal(35, result.Entries[0].Id);
        Assert.Equal(1222, result.Entries[0].ObjectId);
        Assert.Equal(".300BLK_4", result.Entries[0].Name);
        Assert.Equal("4级 .300BLK", result.Entries[0].Label);
        Assert.Equal(17, result.Entries[1].Hour);
        Assert.EndsWith("/1222.png", result.Entries[1].ImageUrl);
        Assert.True(result.IsCurrent(Now));
        Assert.False(result.IsCurrent(new DateTimeOffset(2026, 9, 29, 16, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("\"grade\":4", "\"grade\":5")]
    [InlineData("\"hour\":17", "\"hour\":24")]
    [InlineData("\"hour\":17", "\"hour\":0")]
    [InlineData("\"price\":1687", "\"price\":-1")]
    [InlineData("\"oid\":1222", "\"oid\":0")]
    [InlineData("\"price\":1650", "\"price\":1000000000000")]
    public void Rejects_invalid_provider_data(string from, string to) =>
        Assert.Throws<InvalidOperationException>(() => AmmoMarketClient.Parse(4, Forecast.Replace(from, to), Now));

    [Fact]
    public async Task Single_refresh_uses_selected_grade_without_llm_or_history_requests()
    {
        int count = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            count++;
            Assert.Equal("orzice.com", request.RequestUri!.Host);
            Assert.Equal("/workApi/v1/sjz_api/ammo_day", request.RequestUri.AbsolutePath);
            Assert.Contains("grade=4", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(Forecast) };
        }));
        var client = new AmmoMarketClient(http);
        Assert.Equal(0, count);
        await client.FetchLowPricesAsync(4, Token, default);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Errors_do_not_expose_credentials_or_retry()
    {
        int count = 0;
        using var http = new HttpClient(new Handler(_ => { count++; throw new HttpRequestException(Token); }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AmmoMarketClient(http).FetchLowPricesAsync(4, Token, default));
        Assert.DoesNotContain(Token, error.ToString());
        Assert.Equal(1, count);
        error = Assert.ThrowsAny<InvalidOperationException>(() => AmmoMarketClient.Parse(4, "{\"code\":403,\"msg\":\"" + Token + "\"}", Now));
        Assert.DoesNotContain(Token, error.ToString());
    }

    [Fact]
    public async Task History_uses_object_id_and_validates_pairs()
    {
        const string json = """{"code":0,"data":{"a":["09-29 01:00","09-29 01:10","09-29 01:20"],"b":[100,0,120]}}""";
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/workApi/v1/sjz_api/minute", request.RequestUri!.AbsolutePath);
            Assert.Contains("id=1222", request.RequestUri.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }));
        var history = await new AmmoMarketClient(http).FetchHistoryAsync(1222, Token, default);
        Assert.Equal(2, history.Points.Count);
        Assert.Equal(120m, history.Points[^1].Price);
        Assert.Throws<InvalidOperationException>(() => AmmoMarketClient.ParseHistory(1222, json.Replace("100,0,120", "100,120"), Now));
    }

    [Fact]
    public async Task Cached_reads_never_query_and_failed_refresh_preserves_previous_data()
    {
        var source = new Source();
        var coordinator = new QuantCoordinator(source, new(), FilePath);
        Assert.Empty(coordinator.Snapshot(Now).Forecasts);
        Assert.Equal(0, source.Calls);
        await coordinator.RefreshAsync(4, Token, default);
        string saved = File.ReadAllText(FilePath);
        source.Fetch = _ => throw new InvalidOperationException("offline");
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(4, Token, default));
        Assert.Equal(saved, File.ReadAllText(FilePath));
        Assert.Single(coordinator.Snapshot(Now).Forecasts);
        Assert.Single(new QuantCoordinator(source, new(), FilePath).Snapshot(Now).Forecasts);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Concurrent_refresh_does_not_charge_for_queued_redundant_request()
    {
        var pending = new TaskCompletionSource<AmmoForecast>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Fetch = ct => pending.Task.WaitAsync(ct) };
        var coordinator = new QuantCoordinator(source, new(), FilePath);
        using var cancel = new CancellationTokenSource();
        Task first = coordinator.RefreshAsync(4, Token, cancel.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(4, Token, default));
        Assert.Equal(1, source.Calls);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Empty(coordinator.Snapshot(Now).Forecasts);
        source.Fetch = _ => Task.FromResult(AmmoMarketClient.Parse(4, Forecast, Now));
        await coordinator.RefreshAsync(4, Token, default);
        Assert.Single(coordinator.Snapshot(Now).Forecasts);
    }

    [Fact]
    public void Ledger_supports_partial_sales_and_tax_without_treating_forecast_as_profit()
    {
        var coordinator = new QuantCoordinator(new Source(), new(), FilePath);
        var id = Guid.NewGuid();
        coordinator.RecordPurchase(new(id, 35, ".300BLK_4", 4, 100, 100, Now));
        Assert.Equal(10000, coordinator.Snapshot(Now).Summary.OpenCost);
        Assert.Equal(0, coordinator.Snapshot(Now).Summary.RealizedProfit);
        coordinator.RecordSale(id, 40, 150, 10, Now.AddMinutes(5));
        var state = coordinator.Snapshot(Now);
        Assert.Equal(6000, state.Summary.OpenCost);
        Assert.Equal(1400, state.Summary.RealizedProfit);
        Assert.Equal(100, state.Summary.WinRate);
        Assert.Equal(60, Assert.Single(state.Trades, t => !t.IsClosed).Quantity);
        Assert.Throws<InvalidOperationException>(() => coordinator.RecordSale(id, 1, 150, 10, Now));
        var reopened = new QuantCoordinator(new Source(), new(), FilePath);
        Assert.Equal(state.Summary, reopened.Snapshot(Now).Summary);
    }

    [Fact]
    public void Invalid_trade_does_not_change_file_or_memory()
    {
        var coordinator = new QuantCoordinator(new Source(), new(), FilePath);
        string before = File.ReadAllText(FilePath);
        Assert.Throws<ArgumentException>(() => coordinator.RecordPurchase(new(Guid.NewGuid(), 35, "子弹", 4, 0, 100, Now)));
        Assert.Empty(coordinator.Snapshot(Now).Trades);
        Assert.Equal(before, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Failed_persistence_does_not_publish_unsaved_state()
    {
        var coordinator = new QuantCoordinator(new Source(), new(), FilePath);
        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);
        Assert.ThrowsAny<IOException>(() => coordinator.SaveWatch(new(35, "子弹", 4, 100, 150, 10, 13)));
        Assert.Empty(coordinator.Snapshot(Now).Watchlist);
    }

    [Fact]
    public void Corrupt_workspace_is_not_silently_replaced()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{broken");
        Assert.Throws<InvalidDataException>(() => new QuantCoordinator(new Source(), new(), FilePath));
        Assert.Equal("{broken", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Profit_budget_and_drawdown_use_net_actual_sales()
    {
        var plan = new AmmoWatchItem(1, "子弹", 4, 100, 150, 10, 13);
        Assert.Equal(305, plan.NetProfit);
        Assert.Equal(30.5m, plan.NetReturnPercent);
        Assert.Equal(9, QuantAccounting.AffordableQuantity(999, 100));
        var trades = new[]
        {
            new AmmoTrade(Guid.NewGuid(), 1, "子弹", 4, 10, 100, Now, 150, 10, Now.AddMinutes(1)),
            new AmmoTrade(Guid.NewGuid(), 1, "子弹", 4, 10, 100, Now, 80, 0, Now.AddMinutes(2)),
        };
        var result = QuantAccounting.Summarize(trades, Now);
        Assert.Equal(150, result.RealizedProfit);
        Assert.Equal(200, result.MaxDrawdown);
        Assert.Equal(50, result.WinRate);
    }

    [Fact]
    public void Indicators_require_real_samples_and_handle_flat_rising_falling_prices()
    {
        Assert.Null(QuantIndicators.Calculate([]));
        var flat = Enumerable.Repeat(new AmmoPricePoint("t", 100), 20).ToArray();
        var result = QuantIndicators.Calculate(flat)!;
        Assert.Equal(50, result.Rsi);
        Assert.Equal(0, result.Volatility);
        Assert.Equal(50, result.PositionPercent);
        var rising = Enumerable.Range(100, 20).Select(p => new AmmoPricePoint("t", p)).ToArray();
        result = QuantIndicators.Calculate(rising)!;
        Assert.Equal(100, result.Rsi);
        Assert.Equal(109.5m, result.Mean);
        Assert.Equal(119, result.High);
        Assert.Equal(19, result.ChangePercent);
        Assert.Equal(0, QuantIndicators.Calculate(rising.Reverse().ToArray())!.Rsi);
    }

    [Theory]
    [InlineData("http://orzice.com/update/sjz_pic/1.png")]
    [InlineData("https://orzice.com.evil.test/update/sjz_pic/1.png")]
    [InlineData("https://user@orzice.com/update/sjz_pic/1.png")]
    [InlineData("https://orzice.com:8443/update/sjz_pic/1.png")]
    public void Rejects_untrusted_image_urls(string url) => Assert.Empty(AmmoMarketClient.SafeImageUrl(url));
}
