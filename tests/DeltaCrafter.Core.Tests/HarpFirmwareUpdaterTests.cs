using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Serilog;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class HarpFirmwareUpdaterTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static readonly HarpPlayerSettings Connection = new() { Url="http://192.168.1.123", ApiKey=Key };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "harp-push-" + Guid.NewGuid().ToString("N"));
    private readonly DeviceFirmwareStore _store;
    private readonly DeviceFirmwareManifest _manifest;
    private readonly byte[] _image = new byte[9000];
    public HarpFirmwareUpdaterTests()
    {
        _store = new(_root, DeviceFirmwareTarget.Harp);
        _image[0]=0xe9; _image[12]=9;
        "DeltaHarp:esp32-s3-dongle-fn8:dual-8mb-v1:app"u8.CopyTo(_image.AsSpan(64));
        // Base64 with many '+' characters reproduced a real-device failure at offset 724992.
        // HTML escaping must not inflate a 4096-byte chunk beyond the firmware's JSON limit.
        _image.AsSpan(4096,4096).Fill(0xfb);
        _manifest = Import(2);
    }
    private DeviceFirmwareManifest Import(int revision)
    {
        string zipPath = Path.Combine(_root,$"{revision}.zip");
        using (var zip = ZipFile.Open(zipPath,ZipArchiveMode.Create))
        {
            using (var stream=zip.CreateEntry("firmware.bin").Open()) stream.Write(_image);
            using var manifest=zip.CreateEntry("ota-manifest.json").Open();
            JsonSerializer.Serialize(manifest,new HarpFirmwareBundle(1,"esp32-s3-dongle-fn8","dual-8mb-v1",$"delta-harp-v{revision}",
                _image.Length,Convert.ToHexString(SHA256.HashData(_image)).ToLowerInvariant()),new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        return _store.Import(zipPath);
    }
    public void Dispose() => Directory.Delete(_root,true);
    private HarpFirmwareUpdater Updater(FakePlayer fake)
    {
        var updater=new HarpFirmwareUpdater(_store,fake,TimeSpan.FromMilliseconds(600));
        Assert.True(updater.Configure(Connection)); return updater;
    }
    private static HarpUpdateRequest Command(string action,HarpUpdateState? state=null) =>
        new(action,Guid.NewGuid().ToString("N"),state?.CheckId ?? "",Connection.Url);
    private static async Task<HarpUpdateState> Done(HarpFirmwareUpdater updater)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while(updater.State.Busy) await Task.Delay(10,timeout.Token);
        return updater.State;
    }
    private static async Task<HarpUpdateRequest> Check(HarpFirmwareUpdater updater)
    {
        Assert.Equal(202,updater.Submit(Command("check")).Code);
        Assert.True((await Done(updater)).Ready);
        return Command("install",updater.State);
    }

    [Theory]
    [InlineData("normal",3)]
    [InlineData("lost-chunk",3)]
    [InlineData("retry-chunk",4)]
    [InlineData("lost-commit",3)]
    public async Task Desktop_pushes_bounded_chunks_and_resolves_lost_responses(string mode,int sends)
    {
        var fake=new FakePlayer { Mode=mode };
        using var updater=Updater(fake);
        var install=await Check(updater);
        Import(3); // The checked v2 remains pinned while the desktop imports a newer package.
        Assert.Equal(202,updater.Submit(install).Code);
        Assert.Equal("complete",(await Done(updater)).Phase);
        Assert.Equal(_image,fake.Bytes.ToArray()); Assert.Equal(sends,fake.Sends);
        Assert.Equal(1,fake.Commits); Assert.Equal(0,fake.Aborts);
        Assert.Equal(200,updater.Submit(install).Code); Assert.Equal(1,fake.Begins);
        Assert.Equal("delta-harp-v2",updater.State.Version);
    }

    [Theory]
    [InlineData("chunk-failed",true,0)]
    [InlineData("wrong-session",true,0)]
    [InlineData("commit-denied",true,1)]
    [InlineData("no-reboot",false,1)]
    [InlineData("sd-mounted",false,0)]
    public async Task Failed_push_is_not_reported_complete(string mode,bool aborted,int commits)
    {
        var fake=new FakePlayer { Mode=mode }; using var updater=Updater(fake);
        var install=await Check(updater); updater.Submit(install);
        Assert.Equal("error",(await Done(updater)).Phase);
        Assert.False(updater.State.Ready); Assert.Equal(aborted,fake.Aborts>0); Assert.Equal(commits,fake.Commits);
        Assert.DoesNotContain(Key,JsonSerializer.Serialize(updater.State));
    }

    [Fact]
    public async Task Device_prepares_storage_via_begin_API_and_client_retries_only_busy_responses()
    {
        var fake=new FakePlayer {Mode="sd-busy-once"}; using var updater=Updater(fake);
        updater.Submit(await Check(updater));
        Assert.Equal("complete",(await Done(updater)).Phase);
        Assert.Equal(2,fake.Begins); Assert.Equal(_image,fake.Bytes.ToArray());
    }

    [Fact]
    public async Task Disconnecting_controller_does_not_cancel_the_desktop_job_and_duplicate_start_is_safe()
    {
        var fake=new FakePlayer { Hold=true }; using var updater=Updater(fake);
        var install=await Check(updater); updater.Submit(install);
        await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(200,updater.Submit(install).Code);
        Assert.Equal(409,updater.Submit(Command("check")).Code);
        Assert.False(updater.Configure(Connection with {Url="http://192.168.1.124"}));
        fake.Release.TrySetResult();
        Assert.Equal("complete",(await Done(updater)).Phase); Assert.Equal(1,fake.Begins);
    }

    [Fact]
    public async Task Cancelling_desktop_aborts_before_commit()
    {
        var fake=new FakePlayer { Hold=true }; using var updater=Updater(fake);
        updater.Submit(await Check(updater)); await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        updater.Dispose();
        Assert.Equal("error",(await Done(updater)).Phase); Assert.Equal(1,fake.Aborts); Assert.Equal(0,fake.Commits);
    }

    [Fact]
    public async Task Tampering_or_changing_pairing_invalidates_install()
    {
        var fake=new FakePlayer(); using var updater=Updater(fake);
        var install=await Check(updater);
        Assert.Equal(409,updater.Submit(install with {PlayerUrl="http://192.168.1.124"}).Code);
        string path=Path.Combine(_root,_manifest.BundleId,"firmware.bin");
        var bytes=File.ReadAllBytes(path); bytes[^1]^=1; File.WriteAllBytes(path,bytes);
        updater.Submit(install); Assert.Equal("error",(await Done(updater)).Phase); Assert.Equal(0,fake.Begins);
        Assert.True(updater.Configure(Connection with {ApiKey=new string('b',32)}));
        Assert.Equal(409,updater.Submit(install with {RequestId=Guid.NewGuid().ToString("N")}).Code);
    }

    [Theory]
    [InlineData("wrong-board")]
    [InlineData("downgrade")]
    [InlineData("unauthorized")]
    public async Task Check_rejects_incompatible_or_unauthorized_player(string mode)
    {
        var fake=new FakePlayer {Mode=mode}; using var updater=Updater(fake);
        updater.Submit(Command("check")); Assert.Equal("error",(await Done(updater)).Phase); Assert.Equal(0,fake.Begins);
    }

    [Theory]
    [InlineData("http://192.168.1.2",true)]
    [InlineData("http://10.0.0.194:80/",true)]
    [InlineData("http://172.16.0.2",true)]
    [InlineData("http://127.0.0.1",false)]
    [InlineData("http://8.8.8.8",false)]
    [InlineData("http://user:password@192.168.1.2",false)]
    [InlineData("http://192.168.1.2/api",false)]
    [InlineData("http://example.com",false)]
    public void Pairing_accepts_only_explicit_LAN_targets(string url,bool expected) => Assert.Equal(expected,(Connection with {Url=url}).IsValid());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Desktop_job_routes_enforce_pairing_and_control(bool allow)
    {
        var fake=new FakePlayer(); using var updater=Updater(fake);
        using var listener=new TcpListener(IPAddress.Loopback,0); listener.Start();
        int port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        using var server=new DeviceApiServer(new() {ApiKey=Key,Port=port,AllowControl=allow},
            _ => throw new NotSupportedException(), (_,_) => throw new NotSupportedException(),new LoggerConfiguration().CreateLogger(),harpUpdater:updater);
        server.Start();
        using var client=new HttpClient(new HttpClientHandler {UseProxy=false}) {BaseAddress=new Uri($"http://127.0.0.1:{port}")};
        using(var denied=await client.PostAsJsonAsync("/api/v1/harp/update/action",Command("check"))) Assert.Equal(HttpStatusCode.Unauthorized,denied.StatusCode);
        client.DefaultRequestHeaders.Authorization=new("Bearer",Key);
        using(var status=await client.GetAsync("/api/v1/harp/update")) Assert.Equal(allow?HttpStatusCode.OK:HttpStatusCode.Forbidden,status.StatusCode);
        using(var submit=await client.PostAsJsonAsync("/api/v1/harp/update/action",Command("check"))) Assert.Equal(allow?HttpStatusCode.Accepted:HttpStatusCode.Forbidden,submit.StatusCode);
        if(allow)
        {
            Assert.True((await Done(updater)).Ready);
            using var wrongMethod=await client.GetAsync("/api/v1/harp/update/action"); Assert.Equal(HttpStatusCode.MethodNotAllowed,wrongMethod.StatusCode);
            using var invalid=await client.PostAsJsonAsync("/api/v1/harp/update/action",new {action="install"}); Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        }
    }

    private sealed class FakePlayer : HttpMessageHandler
    {
        public string Mode="normal";
        public bool Hold;
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly MemoryStream Bytes=new();
        public int Begins,Sends,Commits,Aborts;
        private bool _rebooted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(Connection.Url,request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal("Bearer",request.Headers.Authorization!.Scheme); Assert.Equal(Key,request.Headers.Authorization.Parameter);
            if(request.Method==HttpMethod.Post) Assert.True(request.Content!.Headers.ContentLength>0);
            string path=request.RequestUri.AbsolutePath;
            int code=200; string error="";
            if(path.EndsWith("/begin")) { ++Begins; if(Mode=="sd-mounted") {code=409;error="eject_sd_on_computer_first";} if(Mode=="sd-busy-once" && Begins==1) {code=409;error="sd_io_busy";} }
            else if(path.EndsWith("/chunk"))
            {
                ++Sends; Entered.TrySetResult(); if(Hold) await Release.Task.WaitAsync(ct);
                var json=await request.Content!.ReadAsByteArrayAsync(ct);
                Assert.InRange(json.Length,1,6144);
                using var body=JsonDocument.Parse(json);
                byte[] bytes=Convert.FromBase64String(body.RootElement.GetProperty("data").GetString()!);
                Assert.InRange(bytes.Length,1,4096); Assert.Equal(Bytes.Length,body.RootElement.GetProperty("offset").GetInt32());
                if(Mode is "chunk-failed" or "wrong-session" || Mode=="retry-chunk" && Sends==1) throw new HttpRequestException();
                Bytes.Write(bytes);
                if(Mode=="lost-chunk" && Sends==1) throw new HttpRequestException();
            }
            else if(path.EndsWith("/commit"))
            {
                ++Commits;
                if(Mode=="commit-denied") code=400;
                else if(Mode!="no-reboot") _rebooted=true;
                if(Mode=="lost-commit") throw new HttpRequestException();
            }
            else if(path.EndsWith("/abort")) ++Aborts;
            if(Mode=="unauthorized") code=401;
            return new((HttpStatusCode)code) {Content=JsonContent.Create(new {
                ok=code==200, error, deviceId="68ee8f6d4a44", version=Mode=="downgrade"?"delta-harp-v3":"delta-harp-v2",
                board=Mode=="wrong-board"?"wrong":"esp32-s3-dongle-fn8",layout="dual-8mb-v1",partition=_rebooted?"app1":"app0",
                phase=_rebooted?"idle":"receiving",maxBytes=0x330000,chunkBytes=4096,
                updateId=Mode=="wrong-session" && Sends>0?"other":"session",received=Bytes.Length })};
        }
    }
}
