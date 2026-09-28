using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class DeviceApiTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T12:00:00Z");

    private static DeviceStatus Status(bool autoLoop = true)
    {
        var state = ScheduleState.CreateDefault();
        var workbench = state.For(FacilityKey.Workbench);
        workbench.Phase = FacilityPhase.Crafting;
        workbench.ItemName = "测试物品";
        workbench.ReadyAt = Now.AddMilliseconds(1500);
        workbench.StartedAt = Now.AddMilliseconds(-8500);
        state.For(FacilityKey.PharmacyLab).Phase = FacilityPhase.Crafting;
        state.For(FacilityKey.PharmacyLab).ReadyAt = Now.AddSeconds(-10);
        state.For(FacilityKey.ArmorStation).Phase = FacilityPhase.NeedsManual;
        state.For(FacilityKey.ArmorStation).ReadyAt = Now.AddHours(1);
        var plan = CraftPlanConfig.CreateDefault();
        plan.For(FacilityKey.Workbench).Enabled = true;
        plan.For(FacilityKey.Workbench).ItemName = "计划物品";
        return DeviceApiCoordinator.CreateStatus("0.4.0", Now,
            new(EngineMode.WaitingSchedule, "等待下次执行", Now.AddSeconds(120)), false,
            new AppSettings { AutoLoopEnabled = autoLoop }, plan, state);
    }

    [Fact]
    public void Snapshot_keeps_observed_and_planned_items_and_ocr_phase_separate()
    {
        var status = Status();
        Assert.Equal(4, status.Facilities.Count);
        Assert.Equal("workbench", status.Facilities[0].Key);
        Assert.Equal("测试物品", status.Facilities[0].ItemName);
        Assert.Equal("计划物品", status.Facilities[0].PlannedItemName);
        Assert.Equal(2, status.Facilities[0].RemainingSeconds);
        Assert.Equal(10, status.Facilities[0].TotalSeconds);
        Assert.Null(status.Facilities[1].TotalSeconds);
        Assert.Equal(0, status.Facilities[1].RemainingSeconds);
        Assert.Equal("Crafting", status.Facilities[1].Phase); // 倒计时归零不能假称已领取就绪。
        Assert.Null(status.Facilities[2].RemainingSeconds);
        Assert.Null(status.Facilities[3].RemainingSeconds);
        Assert.Equal(120, status.NextRunInSeconds);
        Assert.Null(Status(autoLoop: false).NextRunAt);
        Assert.Null(Status(autoLoop: false).NextRunInSeconds);
    }

    [Theory]
    [InlineData(1023, Key)]
    [InlineData(65536, Key)]
    [InlineData(17890, "")]
    [InlineData(17890, "short")]
    [InlineData(17890, "0123456789abcdef0123456789abcde\n")]
    public void Rejects_invalid_listener_configuration(int port, string key) =>
        Assert.Throws<ArgumentException>(() => new DeviceApiSettings { Port = port, ApiKey = key }.Validate());

    private sealed class Server : IDisposable
    {
        public DeviceApiServer Api { get; }
        public HttpClient Client { get; }
        public List<string> Actions { get; } = [];
        public List<DeviceSettingsRequest> Updates { get; } = [];

        public Server(bool control = false,
            Func<CancellationToken, Task<DeviceStatus>>? getStatus = null)
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            int port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Api = new DeviceApiServer(new DeviceApiSettings { ApiKey = Key, Port = port, AllowControl = control },
                getStatus ?? (_ => Task.FromResult(Status())), (action, _) =>
                {
                    Actions.Add(action);
                    return Task.FromResult(new DeviceActionResult(202, "accepted"));
                }, new LoggerConfiguration().CreateLogger(), (update, _) =>
                {
                    Updates.Add(update);
                    return Task.FromResult(new DeviceActionResult(200, "saved"));
                });
            Api.Start();
            Client = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}"),
                Timeout = TimeSpan.FromSeconds(10),
            };
            Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        }

        public Task<HttpResponseMessage> Post(string body, string contentType = "application/json") =>
            Client.PostAsync("/api/v1/action", new StringContent(body, Encoding.UTF8, contentType));

        public void Dispose() { Client.Dispose(); Api.Dispose(); }
    }

    [Fact]
    public async Task Status_requires_pairing_key_and_returns_firmware_contract_without_secrets()
    {
        using var server = new Server();
        server.Client.DefaultRequestHeaders.Authorization = null;
        using var missing = await server.Client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        server.Client.DefaultRequestHeaders.Authorization = new("Bearer", new string('x', 32));
        using var wrong = await server.Post("{\"action\":\"start\"}");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Empty(server.Actions);
        server.Client.DefaultRequestHeaders.Authorization = new("Bearer", Key);
        using var response = await server.Client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.Equal("workbench", doc.RootElement.GetProperty("facilities")[0].GetProperty("key").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("facilities")[0].GetProperty("remainingSeconds").GetInt32());
        Assert.Equal("测试物品", doc.RootElement.GetProperty("facilities")[0].GetProperty("itemName").GetString());
        Assert.DoesNotContain(Key, json);
        Assert.DoesNotContain("gamePath", json);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("sync")]
    public async Task Read_only_mode_never_dispatches_actions(string action)
    {
        using var server = new Server();
        using var response = await server.Post(JsonSerializer.Serialize(new { action }));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(server.Actions);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("sync")]
    [InlineData("stop")]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task Valid_action_is_dispatched_once(string action)
    {
        using var server = new Server(control: true);
        using var response = await server.Post(JsonSerializer.Serialize(new { action }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new[] { action }, server.Actions);
    }

    [Theory]
    [InlineData("{", "application/json", 400)]
    [InlineData("null", "application/json", 400)]
    [InlineData("{}", "application/json", 400)]
    [InlineData("{\"action\":\"delete\"}", "application/json", 400)]
    [InlineData("{\"action\":\"start\"}", "text/plain", 415)]
    public async Task Invalid_requests_do_not_reach_control(string body, string type, int code)
    {
        using var server = new Server(control: true);
        using var response = await server.Post(body, type);
        Assert.Equal(code, (int)response.StatusCode);
        Assert.Empty(server.Actions);
    }

    [Fact]
    public async Task Oversized_body_routes_and_methods_are_rejected()
    {
        using var server = new Server(control: true);
        using var big = await server.Post(new string(' ', 1025));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);
        using var wrongMethod = await server.Client.GetAsync("/api/v1/action");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        using var missingRoute = await server.Client.GetAsync("/api/v1/unknown");
        Assert.Equal(HttpStatusCode.NotFound, missingRoute.StatusCode);
        using var healthy = await server.Client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        Assert.Empty(server.Actions);
    }

    [Fact]
    public void Disabling_api_releases_listener_and_allows_rebind()
    {
        using var server = new Server();
        int port = server.Client.BaseAddress!.Port;
        server.Api.Dispose();
        using var second = new DeviceApiServer(new DeviceApiSettings { ApiKey = Key, Port = port },
            _ => Task.FromResult(Status()), (_, _) => throw new InvalidOperationException(),
            new LoggerConfiguration().CreateLogger());
        second.Start();
    }

    [Fact]
    public async Task Chunked_body_cannot_bypass_size_limit()
    {
        using var server = new Server(control: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/action")
        {
            Content = new StringContent(new string(' ', 2048), Encoding.UTF8, "application/json"),
        };
        request.Headers.TransferEncodingChunked = true;
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(server.Actions);
    }

    [Fact]
    public async Task Callback_error_is_reported_and_listener_remains_usable()
    {
        int calls = 0;
        using var server = new Server(getStatus: _ => Interlocked.Increment(ref calls) == 1
            ? throw new InvalidOperationException("private diagnostic") : Task.FromResult(Status()));
        using var failure = await server.Client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.InternalServerError, failure.StatusCode);
        Assert.DoesNotContain("private diagnostic", await failure.Content.ReadAsStringAsync());
        using var success = await server.Client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
    }

    [Fact]
    public async Task Disabling_api_cancels_inflight_snapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new Server(getStatus: async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cancelled.SetResult(); }
            return Status();
        });
        var response = server.Client.GetAsync("/api/v1/status");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.Api.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Close may abort the socket or produce a non-success HTTP response, platform dependent.
        try { using var result = await response; }
        catch (HttpRequestException) { }
    }

    [Theory]
    [InlineData("{\"facility\":\"tech-center\",\"enabled\":false}")]
    [InlineData("{\"facility\":\"pharmacy-lab\",\"craftMode\":\"HourlyProfit\"}")]
    [InlineData("{\"autoLoopEnabled\":false}")]
    [InlineData("{\"steamDetectionEnabled\":true}")]
    [InlineData("{\"afterRun\":\"KeepAtLobby\"}")]
    public async Task Settings_require_control_and_dispatch_one_valid_update(string body)
    {
        using var readOnly = new Server();
        using var denied = await readOnly.Client.PostAsync("/api/v1/settings", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(readOnly.Updates);
        using var writable = new Server(control: true);
        using var saved = await writable.Client.PostAsync("/api/v1/settings", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Single(writable.Updates);
        Assert.Empty(writable.Actions);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"facility\":\"unknown\",\"enabled\":true}")]
    [InlineData("{\"facility\":\"workbench\",\"craftMode\":\"invalid\"}")]
    [InlineData("{\"facility\":\"workbench\",\"enabled\":true,\"craftMode\":\"Custom\"}")]
    [InlineData("{\"facility\":\"workbench\",\"enabled\":true,\"autoLoopEnabled\":true}")]
    [InlineData("{\"facility\":\"workbench\",\"plannedItemName\":\"not remotely editable\"}")]
    [InlineData("{\"afterRun\":\"Delete\"}")]
    [InlineData("{\"autoLoopEnabled\":true,\"steamDetectionEnabled\":false}")]
    [InlineData("{\"enabled\":true}")]
    public async Task Invalid_or_mixed_settings_never_mutate_state(string body)
    {
        using var server = new Server(control: true);
        using var response = await server.Client.PostAsync("/api/v1/settings", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(server.Updates);
    }

    [Fact]
    public async Task Settings_are_authenticated_and_game_status_is_distinct_from_task_state()
    {
        var game = new DeviceGameStatus("Playing", "Steam 游戏中: Delta Force", Now);
        using var server = new Server(control: true, getStatus: _ => Task.FromResult(Status() with { Game = game }));
        using var status = await server.Client.GetAsync("/api/v1/status");
        using var json = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal("Playing", json.RootElement.GetProperty("game").GetProperty("state").GetString());
        Assert.False(json.RootElement.GetProperty("isRunning").GetBoolean());
        Assert.True(json.RootElement.GetProperty("settingsSupported").GetBoolean());
        server.Client.DefaultRequestHeaders.Authorization = null;
        using var response = await server.Client.PostAsync("/api/v1/settings",
            new StringContent("{\"autoLoopEnabled\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(server.Updates);
    }
}
