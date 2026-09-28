using System.Net;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L2;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class SteamActivityTests
{
    private const string Key = "0123456789ABCDEF0123456789ABCDEF";
    private const string Id = "76561198000000001";

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private static SteamActivityClient Client(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(TimeSpan.FromSeconds(1), () => new Handler((r, _) =>
        {
            Assert.Equal("https", r.RequestUri!.Scheme);
            Assert.Equal("api.steampowered.com", r.RequestUri.Host);
            Assert.Equal("/ISteamUser/GetPlayerSummaries/v0002/", r.RequestUri.AbsolutePath);
            Assert.Contains("steamids=" + Id, r.RequestUri.Query);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }));

    private static string Player(string fields) =>
        "{\"response\":{\"players\":[{\"steamid\":\"" + Id + "\"," + fields + "}]}}";

    [Theory]
    [InlineData("\"gameid\":\"2507950\",\"gameextrainfo\":\"Delta Force\"")]
    [InlineData("\"gameid\":\"2507950\",\"personastate\":0")]
    [InlineData("\"gameextrainfo\":\"三角洲行动\"")]
    public async Task Delta_force_blocks_even_when_persona_is_offline(string fields)
    {
        var result = await Client(Player(fields)).CheckAsync(Key, Id, default);
        Assert.Equal(SteamActivityState.Playing, result.State);
        Assert.Equal("游戏中", result.Detail);
    }

    [Theory]
    [InlineData("\"gameid\":\"570\",\"personastate\":0")]
    [InlineData("\"gameextrainfo\":\"Non-Steam Game\"")]
    [InlineData("\"gameid\":\"570\",\"gameextrainfo\":\"Delta Force\"")]
    [InlineData("\"gameextrainfo\":\"Delta Force 2\"")]
    public async Task Other_games_do_not_block_or_show_their_names(string fields)
    {
        var result = await Client(Player(fields)).CheckAsync(Key, Id, default);
        Assert.Equal(SteamActivityState.NotPlaying, result.State);
        Assert.Equal("未在游戏中", result.Detail);
    }

    [Fact]
    public async Task Online_persona_without_game_is_not_playing()
    {
        var result = await Client(Player("\"communityvisibilitystate\":3,\"personastate\":1"))
            .CheckAsync(Key, Id, default);
        Assert.Equal(SteamActivityState.NotPlaying, result.State);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"response\":{\"players\":[]}}")]
    [InlineData("{\"response\":{\"players\":[{\"steamid\":\"wrong-user\",\"communityvisibilitystate\":3}]}}")]
    public async Task Missing_or_malformed_response_does_not_allow_tasks(string json)
    {
        Assert.Equal(SteamActivityState.Unavailable, (await Client(json).CheckAsync(Key, Id, default)).State);
    }

    [Theory]
    [InlineData("\"communityvisibilitystate\":1")]
    [InlineData("\"communityvisibilitystate\":\"3\"")]
    [InlineData("\"communityvisibilitystate\":3,\"gameid\":123")]
    public async Task Private_or_invalid_game_data_does_not_allow_tasks(string fields)
    {
        Assert.Equal(SteamActivityState.Unavailable, (await Client(Player(fields)).CheckAsync(Key, Id, default)).State);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task Http_error_is_sanitized(HttpStatusCode status)
    {
        var result = await Client(Key, status).CheckAsync(Key, Id, default);
        Assert.Equal(SteamActivityState.Unavailable, result.State);
        Assert.DoesNotContain(Key, result.Detail);
    }

    [Fact]
    public async Task Transport_exception_does_not_expose_key()
    {
        var client = new SteamActivityClient(TimeSpan.FromSeconds(1), () => new Handler((_, _) =>
            throw new HttpRequestException("URL?key=" + Key)));
        var result = await client.CheckAsync(Key, Id, default);
        Assert.Equal(SteamActivityState.Unavailable, result.State);
        Assert.DoesNotContain(Key, result.Detail);
    }

    [Fact]
    public async Task Timeout_blocks_but_external_cancellation_propagates()
    {
        static Handler Slow() => new(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        });
        var client = new SteamActivityClient(TimeSpan.FromMilliseconds(20), Slow);
        Assert.Equal(SteamActivityState.Unavailable, (await client.CheckAsync(Key, Id, default)).State);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(Key, Id, cts.Token));
    }

    [Theory]
    [InlineData("", Id)]
    [InlineData("not-hex", Id)]
    [InlineData(Key, "123")]
    public async Task Invalid_settings_do_not_send_request(string key, string id)
    {
        var client = new SteamActivityClient(TimeSpan.FromSeconds(1), () => throw new Exception("Must not send"));
        Assert.Equal(SteamActivityState.Unavailable, (await client.CheckAsync(key, id, default)).State);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Source : ISteamActivitySource
    {
        public int Calls { get; private set; }
        public SteamActivityState State = SteamActivityState.Playing;
        public Task<SteamActivityResult> CheckAsync(string key, string id, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new SteamActivityResult(State, "Steam 游戏中"));
        }
    }

    private static SteamActivitySettings Settings() => new() { Enabled = true, ApiKey = Key, SteamId = Id };

    [Fact]
    public async Task Display_polling_is_independent_of_task_guard_and_expires_old_state()
    {
        var clock = new Clock();
        var source = new Source();
        var monitor = new SteamStatusMonitor(source, clock);
        var settings = Settings();
        settings.Enabled = false; // Display can still query when the task guard is disabled.
        Assert.Equal("Unknown", monitor.Snapshot(settings).State);
        await monitor.RefreshAsync(settings, default);
        Assert.Equal("Playing", monitor.Snapshot(settings).State);
        Assert.Equal(clock.Now, monitor.Snapshot(settings).CheckedAt);
        clock.Now = clock.Now.AddSeconds(91);
        Assert.Equal("Unavailable", monitor.Snapshot(settings).State);
        source.State = SteamActivityState.NotPlaying;
        await monitor.RefreshAsync(settings, default);
        Assert.Equal("NotPlaying", monitor.Snapshot(settings).State);
        settings.SteamId = "76561198000000002";
        Assert.Equal("Unknown", monitor.Snapshot(settings).State);
        settings.ApiKey = "";
        Assert.Equal("Unavailable", monitor.Snapshot(settings).State);
    }

    [Theory]
    [InlineData(SteamActivityState.Playing)]
    [InlineData(SteamActivityState.Unavailable)]
    public async Task Blocked_checks_wait_exactly_five_minutes_then_recover(SteamActivityState state)
    {
        var clock = new Clock();
        var source = new Source { State = state };
        var guard = new SteamActivityGuard(source, clock);
        var settings = Settings();
        var block = await guard.CheckAsync(settings, default);
        Assert.Equal(clock.Now.AddMinutes(5), block!.RetryAt);
        clock.Now = block.RetryAt.AddTicks(-1);
        Assert.Same(block, await guard.CheckAsync(settings, default));
        Assert.Equal(1, source.Calls);
        source.State = SteamActivityState.NotPlaying;
        clock.Now = block.RetryAt;
        Assert.Null(await guard.CheckAsync(settings, default));
        Assert.Null(guard.GetBlock(settings));
        Assert.Equal(2, source.Calls);
        await guard.CheckAsync(settings, default);
        Assert.Equal(3, source.Calls); // An allowed response is never cached for another task.
    }

    [Fact]
    public async Task Changed_credentials_or_disabled_guard_do_not_reuse_old_block()
    {
        var source = new Source();
        var guard = new SteamActivityGuard(source, new Clock());
        var settings = Settings();
        await guard.CheckAsync(settings, default);
        settings.SteamId = "76561198000000002";
        Assert.Null(guard.GetBlock(settings));
        await guard.CheckAsync(settings, default);
        Assert.Equal(2, source.Calls);
        settings.Enabled = false;
        Assert.Null(await guard.CheckAsync(settings, default));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task Assistant_owned_game_clears_old_block_and_stops_bypassing_after_exit()
    {
        var source = new Source();
        bool owned = false;
        var guard = new SteamActivityGuard(source, new Clock(), () => owned);
        var settings = Settings();
        Assert.NotNull(await guard.CheckAsync(settings, default)); // Manually started game remains blocked.
        owned = true;
        Assert.Null(guard.GetBlock(settings));
        Assert.Null(await guard.CheckAsync(settings, default));
        Assert.Equal(1, source.Calls);
        owned = false;
        Assert.Null(guard.GetBlock(settings)); // Does not resurrect the old five-minute wait.
        Assert.NotNull(await guard.CheckAsync(settings, default));
        Assert.Equal(2, source.Calls);
        guard.ClearBlock(); // Explicit close clears the cached wait and the next task rechecks Steam.
        Assert.Null(guard.GetBlock(settings));
    }

    [Fact]
    public async Task Coordinator_blocks_all_entry_points_without_touching_windows_or_schedule()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var clock = new Clock();
            var source = new Source();
            var settings = new AppSettings { AutoLoopEnabled = true, SteamActivity = Settings() };
            var plan = CraftPlanConfig.CreateDefault();
            plan.Facilities[0].Enabled = true;
            var log = new LoggerConfiguration().CreateLogger();
            var engine = new ScheduleEngine(new JsonStoreBrick(), path, clock, log);
            var before = File.ReadAllText(path);
            // Null Windows dependencies make any attempted side effect fail this test.
            using var coordinator = new AutomationCoordinator(null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, engine, null!, null!, null!, () => settings, () => plan,
                null!, clock, log, new SteamActivityGuard(source, clock));
            var reports = new[]
            {
                await coordinator.RunOnceAsync("定时触发", default),
                await coordinator.RunOnceAsync("设备 API", default),
                await coordinator.SyncFacilitiesAsync("手动识别", default),
                await coordinator.AbortFacilityAsync(FacilityKey.Workbench),
                await coordinator.DebugEnsureLobbyAsync(),
            };
            Assert.All(reports, report =>
            {
                Assert.False(report.HasFailure);
                Assert.Contains("本次未执行", report.Summary());
            });
            Assert.Equal(1, source.Calls);
            Assert.False(coordinator.IsRunning);
            Assert.Equal(clock.Now.AddMinutes(5), coordinator.WaitingStatus().NextRunAt);
            Assert.Contains("Steam", coordinator.WaitingStatus().Detail);
            Assert.Equal(before, File.ReadAllText(path));
            settings.SteamActivity.Enabled = false;
            Assert.Equal(clock.Now, coordinator.WaitingStatus().NextRunAt);
        }
        finally { File.Delete(path); }
    }
}
