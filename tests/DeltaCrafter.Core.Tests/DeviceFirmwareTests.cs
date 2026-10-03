using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using DeltaCrafter.Core.L3;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class DeviceFirmwareTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delta-firmware-" + Guid.NewGuid().ToString("N"));
    private readonly DeviceFirmwareStore _store;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public DeviceFirmwareTests() => _store = new(Path.Combine(_root, "store"));
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Updating_current_bundle_preserves_old_images_for_in_progress_devices()
    {
        var first = _store.Import(Bundle(16));
        var second = _store.Import(Bundle(17));
        Assert.NotEqual(first.BundleId, second.BundleId);
        using var old = _store.OpenImage(first.BundleId, "monitor");
        Assert.NotNull(old);
        Assert.Equal(Image(16), Read(old));
        var reloaded = new DeviceFirmwareStore(Path.Combine(_root, "store"));
        Assert.Equal(second.BundleId, reloaded.Current!.BundleId);
        Assert.Equal(second.BundleId, reloaded.Import(Bundle(16), onlyNewer: true).BundleId);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("chip")]
    [InlineData("magic")]
    [InlineData("length")]
    [InlineData("board")]
    [InlineData("layout")]
    [InlineData("offset")]
    [InlineData("traversal")]
    [InlineData("duplicate-role")]
    [InlineData("duplicate-entry")]
    [InlineData("missing-image")]
    [InlineData("old-version")]
    [InlineData("updater")]
    public void Invalid_bundle_never_replaces_current_firmware(string corruption)
    {
        var good = _store.Import(Bundle(16));
        Assert.Throws<InvalidDataException>(() => _store.Import(Bundle(17, corruption)));
        Assert.Equal(good.BundleId, _store.Current!.BundleId);
        Assert.Equal(good.BundleId, new DeviceFirmwareStore(Path.Combine(_root, "store")).Current!.BundleId);
        using var image = _store.OpenImage(good.BundleId, "audio");
        Assert.Equal(Image(16), Read(image!));
    }

    [Fact]
    public async Task Firmware_routes_require_pairing_control_and_pin_image_to_bundle()
    {
        var published = _store.Import(Bundle(16));
        using var server = Server(control: true, out var client);
        using (client)
        {
            using var denied = await client.GetAsync("/api/v1/firmware");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var imageDenied = await client.GetAsync($"/api/v1/firmware-image?bundle={published.BundleId}&role=monitor");
            Assert.Equal(HttpStatusCode.Unauthorized, imageDenied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            var manifest = await client.GetFromJsonAsync<DeviceFirmwareManifest>("/api/v1/firmware");
            Assert.Equal(published.BundleId, manifest!.BundleId);
            _store.Import(Bundle(17));
            using var response = await client.GetAsync($"/api/v1/firmware-image?bundle={manifest.BundleId}&role=monitor");
            response.EnsureSuccessStatusCode();
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Image(16), await response.Content.ReadAsByteArrayAsync());
            using var invalid = await client.GetAsync("/api/v1/firmware-image?bundle=..&role=monitor");
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
            using var role = await client.GetAsync($"/api/v1/firmware-image?bundle={manifest.BundleId}&role=settings.json");
            Assert.Equal(HttpStatusCode.NotFound, role.StatusCode);
            using var method = await client.PostAsync("/api/v1/firmware", null);
            Assert.Equal(HttpStatusCode.MethodNotAllowed, method.StatusCode);
        }
    }

    [Fact]
    public async Task Read_only_device_cannot_download_firmware()
    {
        var published = _store.Import(Bundle(16));
        using var server = Server(control: false, out var client);
        using (client)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
            foreach (string path in new[] { "/api/v1/firmware", $"/api/v1/firmware-image?bundle={published.BundleId}&role=audio" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    [Fact]
    public void Incomplete_import_is_ignored_on_restart()
    {
        var published = _store.Import(Bundle(16));
        Directory.CreateDirectory(Path.Combine(_root, "store", ".stage-interrupted"));
        File.WriteAllText(Path.Combine(_root, "store", "current.json.tmp"), "incomplete");
        Assert.Equal(published.BundleId, new DeviceFirmwareStore(Path.Combine(_root, "store")).Current!.BundleId);
        Assert.Null(_store.OpenImage(new string('a', 64), "monitor"));
        Assert.Null(_store.OpenImage("../current.json", "audio"));
    }

    private DeviceApiServer Server(bool control, out HttpClient client)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var server = new DeviceApiServer(new DeviceApiSettings { ApiKey = Key, Port = port, AllowControl = control },
            _ => Task.FromResult(DeviceApiCoordinator.CreateStatus("test", DateTimeOffset.Now,
                new(EngineMode.Idle, "", null), false, new(), CraftPlanConfig.CreateDefault(), ScheduleState.CreateDefault())),
            (_, _) => Task.FromResult(new DeviceActionResult(200, "ok")), new LoggerConfiguration().CreateLogger(), firmware: _store);
        server.Start();
        client = new(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
        return server;
    }

    private string Bundle(int revision, string corruption = "")
    {
        byte[] image = Image(revision);
        if (corruption == "chip") image[12] = 0;
        if (corruption == "magic") image[0] = 0;
        string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        var monitor = new DeviceFirmwareImage("monitor", "firmware.bin", 0x10000, image.Length, hash);
        var audio = new DeviceFirmwareImage("audio", "audio-firmware.bin", 0x650000, image.Length, hash);
        monitor = corruption switch
        {
            "hash" => monitor with { Sha256 = new string('a', 64) },
            "length" => monitor with { Size = 100 },
            "offset" => monitor with { Address = 0xe000 },
            "traversal" => monitor with { File = "../settings.json" },
            "duplicate-role" => audio,
            _ => monitor,
        };
        var manifest = new DeviceFirmwareManifest(1, corruption == "board" ? "esp32" : "lilygo-t-display-s3",
            corruption == "layout" ? "new-layout" : "dual-16mb-v1", $"axeuh-tools-v{revision}",
            corruption == "old-version" ? 15 : revision, corruption == "updater" ? 2 : 1, [monitor, audio]);
        string path = Path.Combine(_root, Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(zip, "ota-manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
        Write(zip, "firmware.bin", image);
        if (corruption != "missing-image") Write(zip, "audio-firmware.bin", image);
        if (corruption == "duplicate-entry") Write(zip, "firmware.bin", image);
        return path;
    }

    private static byte[] Image(int revision)
    {
        var bytes = Enumerable.Repeat((byte)revision, 256).ToArray();
        bytes[0] = 0xe9; bytes[12] = 9; bytes[13] = 0;
        return bytes;
    }
    private static void Write(ZipArchive zip, string name, byte[] bytes)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(bytes);
    }
    private static byte[] Read(Stream stream)
    {
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }
}
