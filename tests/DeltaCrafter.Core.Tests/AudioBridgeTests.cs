using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1.AudioBridge;
using NAudio.Wave;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class AudioBridgeTests
{
    [Fact]
    public async Task Existing_firmware_can_read_toggle_set_mute_and_change_volume()
    {
        await using var server = new Server();
        using var health = await server.Client.GetFromJsonAsync<JsonDocument>("/health");
        Assert.Equal("DeltaCrafter", health!.RootElement.GetProperty("host").GetString());
        var state = await server.Client.GetFromJsonAsync<AudioState>("/api/mute");
        Assert.False(state!.Muted);
        Assert.Equal("speaker", state.DeviceId);

        using var toggled = await server.Client.PostAsync("/api/mute/toggle", null);
        toggled.EnsureSuccessStatusCode();
        Assert.True((await toggled.Content.ReadFromJsonAsync<AudioState>())!.Muted);
        using var unmuted = await server.Client.PutAsJsonAsync("/api/mute", new { muted = false });
        unmuted.EnsureSuccessStatusCode();
        Assert.False((await unmuted.Content.ReadFromJsonAsync<AudioState>())!.Muted);
        using var volume = await server.Client.PutAsJsonAsync("/api/volume", new { volumePercent = 80 });
        volume.EnsureSuccessStatusCode();
        Assert.Equal(80, (await volume.Content.ReadFromJsonAsync<AudioState>())!.VolumePercent);
        using var up = await server.Client.PostAsync("/api/volume/up", null);
        up.EnsureSuccessStatusCode();
        Assert.Equal(85, (await up.Content.ReadFromJsonAsync<AudioState>())!.VolumePercent);
        using var down = await server.Client.PostAsync("/api/volume/down", null);
        down.EnsureSuccessStatusCode();
        Assert.Equal(80, (await down.Content.ReadFromJsonAsync<AudioState>())!.VolumePercent);
        Assert.Equal(80, (await server.Client.GetFromJsonAsync<AudioState>("/api/volume"))!.VolumePercent);
    }

    [Theory]
    [InlineData("PUT", "/api/mute", "{", 400)]
    [InlineData("PUT", "/api/mute", "{}", 400)]
    [InlineData("PUT", "/api/mute", "{\"muted\":\"false\"}", 400)]
    [InlineData("PUT", "/api/volume", "{\"volumePercent\":1e100}", 400)]
    [InlineData("PUT", "/api/volume", "[]", 400)]
    [InlineData("GET", "/api/mute/toggle", "", 405)]
    [InlineData("POST", "/api/mute", "", 405)]
    [InlineData("POST", "/unknown", "", 404)]
    public async Task Invalid_requests_leave_audio_unchanged_and_listener_usable(
        string method, string path, string body, int expected)
    {
        await using var server = new Server();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "PUT") request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await server.Client.SendAsync(request);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(0, server.Audio.Mutations);
        Assert.NotNull(await server.Client.GetFromJsonAsync<AudioState>("/api/mute"));
    }

    [Fact]
    public async Task Oversized_request_is_rejected_without_changing_mute()
    {
        await using var server = new Server();
        using var content = new StringContent(new string(' ', 1025), Encoding.UTF8, "application/json");
        using var response = await server.Client.PutAsync("/api/mute", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, server.Audio.Mutations);
    }

    [Fact]
    public async Task Missing_audio_endpoint_returns_unavailable_and_recovers_on_next_request()
    {
        await using var server = new Server();
        server.Audio.Unavailable = true;
        using var response = await server.Client.GetAsync("/api/mute");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        server.Audio.Unavailable = false;
        Assert.NotNull(await server.Client.GetFromJsonAsync<AudioState>("/api/mute"));
    }

    [Fact]
    public async Task Shutdown_releases_control_port_for_restart()
    {
        int port = FreePort();
        await using (var first = new Server(port))
            Assert.NotNull(await first.Client.GetFromJsonAsync<AudioState>("/api/mute"));
        await using var second = new Server(port);
        Assert.NotNull(await second.Client.GetFromJsonAsync<AudioState>("/api/mute"));
    }

    [Fact]
    public void Imports_old_device_selection_once_and_keeps_legacy_file_untouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "delta-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string legacy = Path.Combine(root, "legacy.json");
            const string original = "{\"SelectedDeviceId\":\"old-s3\"}";
            File.WriteAllText(legacy, original);
            var imported = new BridgeSettingsStore(root, legacy);
            Assert.Equal("old-s3", imported.SelectedDeviceId);
            Assert.Equal(original, File.ReadAllText(legacy));
            imported.SelectedDeviceId = "new-s3";
            Assert.Equal("new-s3", new BridgeSettingsStore(root, legacy).SelectedDeviceId);
            Assert.Equal(original, File.ReadAllText(legacy));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Stereo_downmix_preserves_headroom_and_destination_offset()
    {
        var mono = new MonoSampleProvider(new Samples([1f, 1f, 1f, -1f, .8f, .4f]));
        var output = Enumerable.Repeat(-7f, 6).ToArray();
        Assert.Equal(3, mono.Read(output, 1, 4));
        Assert.Equal(1, mono.WaveFormat.Channels);
        Assert.Equal(-7f, output[0]);
        Assert.Equal(1f, output[1]);
        Assert.Equal(0f, output[2]);
        Assert.Equal(.6f, output[3], 5);
        Assert.Equal(-7f, output[4]);
    }

    private sealed class Samples(float[] samples) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            int copied = Math.Min(count, samples.Length);
            samples.AsSpan(0, copied).CopyTo(buffer.AsSpan(offset));
            return copied;
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private sealed class Server : IAsyncDisposable
    {
        public FakeAudio Audio { get; } = new();
        public ControlServer Api { get; }
        public HttpClient Client { get; }
        public Server(int? port = null)
        {
            int selectedPort = port ?? FreePort();
            Api = new(Audio, () => new { status = "ok", host = "DeltaCrafter" }, selectedPort);
            Api.Start();
            Client = new(new HttpClientHandler { UseProxy = false })
            {
                BaseAddress = new Uri($"http://127.0.0.1:{selectedPort}"),
                Timeout = TimeSpan.FromSeconds(5),
            };
        }
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Api.DisposeAsync();
        }
    }

    private sealed class FakeAudio : IWindowsAudio
    {
        private AudioState _state = new(false, 50, -10, "speaker", DateTimeOffset.UtcNow);
        public int Mutations { get; private set; }
        public bool Unavailable { get; set; }
        public AudioState GetState() => Unavailable
            ? throw new AudioDeviceUnavailableException("No endpoint") : _state;
        public AudioState SetMute(bool muted) { Mutations++; return _state = _state with { Muted = muted }; }
        public AudioState ToggleMute() => SetMute(!_state.Muted);
        public AudioState SetVolume(int volumePercent) { Mutations++; return _state = _state with { VolumePercent = volumePercent }; }
        public AudioState ChangeVolume(int deltaPercent) => SetVolume(_state.VolumePercent + deltaPercent);
    }
}
