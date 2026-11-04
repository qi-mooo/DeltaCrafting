using System.Net;
using System.Text.Json;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class BarkClientTests
{
    private const string Key = "TestDeviceKey0123";

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    [Theory]
    [InlineData("https://bark.example.com/" + Key, "https://bark.example.com/push")]
    [InlineData("https://bark.example.com/" + Key + "/", "https://bark.example.com/push")]
    [InlineData(" http://192.168.1.2:8080/bark/" + Key + " ", "http://192.168.1.2:8080/bark/push")]
    public void Copied_url_splits_into_push_endpoint_and_key(string url, string endpoint)
    {
        Assert.True(BarkClient.TryParse(url, out var uri, out string key));
        Assert.Equal(endpoint, uri.ToString());
        Assert.Equal(Key, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bark.example.com/" + Key)]
    [InlineData("ftp://bark.example.com/" + Key)]
    [InlineData("https://bark.example.com/")]
    [InlineData("https://bark.example.com/short")]
    [InlineData("https://user:pass@bark.example.com/" + Key)]
    [InlineData("https://bark.example.com/bad%20key0123")]
    public void Invalid_url_is_rejected(string url) => Assert.False(BarkClient.TryParse(url, out _, out _));

    [Fact]
    public async Task Sends_json_with_key_in_body_and_urgent_level_for_failures()
    {
        string? body = null;
        Uri? target = null;
        var client = new BarkClient(TimeSpan.FromSeconds(1), () => new Handler(async r =>
        {
            target = r.RequestUri;
            body = await r.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200}") };
        }));
        var result = await client.SendAsync("https://bark.example.com/" + Key, NotificationKind.RunFailed, "标题", "内容", "alarm", default);
        Assert.True(result.Success);
        Assert.Equal("https://bark.example.com/push", target!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(Key, json.RootElement.GetProperty("device_key").GetString());
        Assert.Equal("标题", json.RootElement.GetProperty("title").GetString());
        Assert.Equal("内容", json.RootElement.GetProperty("body").GetString());
        Assert.Equal("timeSensitive", json.RootElement.GetProperty("level").GetString());
        Assert.Equal(BarkClient.IconUrl, json.RootElement.GetProperty("icon").GetString());
        Assert.Equal("alarm", json.RootElement.GetProperty("sound").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-bark-sound")]
    public async Task Default_or_unknown_sound_is_omitted(string sound)
    {
        string? body = null;
        var client = new BarkClient(TimeSpan.FromSeconds(1), () => new Handler(async r =>
        {
            body = await r.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"code\":200}") };
        }));
        await client.SendAsync("https://bark.example.com/" + Key, NotificationKind.CraftStarted, "t", "b", sound, default);
        using var json = JsonDocument.Parse(body!);
        Assert.False(json.RootElement.TryGetProperty("sound", out _));
    }

    [Fact]
    public void Urgent_events_use_urgent_sound()
    {
        var settings = new BarkSettings { Sound = "bell", UrgentSound = "alarm" };
        Assert.Equal("alarm", settings.SoundFor(NotificationKind.RunFailed));
        Assert.Equal("alarm", settings.SoundFor(NotificationKind.NeedsManual));
        Assert.Equal("bell", settings.SoundFor(NotificationKind.CraftStarted));
        Assert.Equal("bell", settings.SoundFor(NotificationKind.SteamBlocked));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"code\":400}")]
    [InlineData(HttpStatusCode.OK, "{\"code\":400,\"message\":\"failed to get device token\"}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task Rejected_push_reports_failure_without_key(HttpStatusCode status, string response)
    {
        var client = new BarkClient(TimeSpan.FromSeconds(1), () => new Handler(_ =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(response) })));
        var result = await client.SendAsync("https://bark.example.com/" + Key, NotificationKind.CraftStarted, "t", "b", "", default);
        Assert.False(result.Success);
        Assert.DoesNotContain(Key, result.Detail);
    }

    [Fact]
    public async Task Transport_exception_does_not_expose_key()
    {
        var client = new BarkClient(TimeSpan.FromSeconds(1), () => new Handler(_ =>
            throw new HttpRequestException("https://bark.example.com/" + Key)));
        var result = await client.SendAsync("https://bark.example.com/" + Key, NotificationKind.CraftStarted, "t", "b", "", default);
        Assert.False(result.Success);
        Assert.DoesNotContain(Key, result.Detail);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void Repeating_reminders_are_limited_per_title_within_window()
    {
        var clock = new Clock();
        var throttle = new NotificationThrottle(clock);
        var settings = new BarkSettings { RepeatLimit = 2, RepeatWindowMinutes = 30 };
        Assert.True(throttle.TryAcquire(NotificationKind.SteamBlocked, "任务已暂缓", settings));
        clock.Now = clock.Now.AddMinutes(5);
        Assert.True(throttle.TryAcquire(NotificationKind.SteamBlocked, "任务已暂缓", settings));
        clock.Now = clock.Now.AddMinutes(5);
        Assert.False(throttle.TryAcquire(NotificationKind.SteamBlocked, "任务已暂缓", settings));
        // Other facilities and non-repeating events have their own budget.
        Assert.True(throttle.TryAcquire(NotificationKind.NeedsManual, "工作台需人工处理", settings));
        Assert.True(throttle.TryAcquire(NotificationKind.NeedsManual, "制药台需人工处理", settings));
        for (int i = 0; i < 5; i++)
            Assert.True(throttle.TryAcquire(NotificationKind.CraftStarted, "工作台开始制造", settings));
        clock.Now = clock.Now.AddMinutes(20); // The first push is now 30 minutes old and leaves the window.
        Assert.True(throttle.TryAcquire(NotificationKind.SteamBlocked, "任务已暂缓", settings));
        Assert.False(throttle.TryAcquire(NotificationKind.SteamBlocked, "任务已暂缓", settings));
    }

    [Fact]
    public void Settings_filter_by_kind_and_master_switch()
    {
        var settings = new BarkSettings { Url = "https://bark.example.com/" + Key };
        Assert.False(settings.Allows(NotificationKind.CraftStarted));
        settings.Enabled = true;
        Assert.True(settings.Allows(NotificationKind.CraftStarted));
        Assert.False(settings.Allows(NotificationKind.ItemCollected));
        settings.ItemCollected = true;
        Assert.True(settings.Allows(NotificationKind.ItemCollected));
    }
}
