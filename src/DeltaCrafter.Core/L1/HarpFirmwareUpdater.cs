using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>Desktop-owned OTA job. Screens only submit actions and read snapshots; losing a screen cannot interrupt a push.</summary>
public sealed class HarpFirmwareUpdater : IDisposable
{
    private const string Board = "esp32-s3-dongle-fn8", Layout = "dual-8mb-v1";
    // These JSON requests go directly to the device, never into HTML. Preserve
    // base64 '+' characters so 4096-byte chunks fit its 6144-byte request limit.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly DeviceFirmwareStore _store;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly TimeSpan _rebootTimeout;
    private HarpPlayerSettings _connection = new();
    private HarpUpdateState _state = new();
    private DeviceFirmwareManifest? _checked;
    private Task _job = Task.CompletedTask;
    private bool _disposed;
    public HarpUpdateState State { get { lock (_gate) return _state; } }
    public event Action? Changed;

    public HarpFirmwareUpdater(DeviceFirmwareStore store, HttpMessageHandler? handler = null, TimeSpan? rebootTimeout = null)
    {
        _store = store;
        _http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) })
            { Timeout = Timeout.InfiniteTimeSpan };
        _rebootTimeout = rebootTimeout ?? TimeSpan.FromSeconds(35);
    }

    public bool Configure(HarpPlayerSettings connection)
    {
        connection = connection with { Url = HarpPlayerSettings.NormalizeUrl(connection.Url) };
        lock (_gate)
        {
            if (_disposed || _state.Busy) return false;
            if (_connection == connection) return true;
            _connection = connection; _checked = null;
            _state = new() { PlayerUrl = connection.Url, PlayerId=connection.DeviceId };
        }
        Changed?.Invoke(); return true;
    }

    public (int Code, HarpUpdateState State) Submit(HarpUpdateRequest command)
    {
        lock (_gate)
        {
            if (!command.IsValid()) return (400, _state with { Detail = "更新请求格式错误" });
            if (_disposed) return (503, _state with { Detail = "客户端正在退出" });
            // One operation per request ID, including a lost response or a completed operation.
            if (_state.RequestId == command.RequestId) return (200, _state);
            if (_state.Busy) return (409, _state);
            if (!_connection.IsValid()) return (409, _state with { Detail = "请在 Windows App 扫描并连接 Harp" });
            if (!string.IsNullOrEmpty(command.PlayerId) ? command.PlayerId != _connection.DeviceId
                : !string.IsNullOrEmpty(command.PlayerUrl) && HarpPlayerSettings.NormalizeUrl(command.PlayerUrl) != _connection.Url)
                return (409, _state with { Detail = "屏幕与 Windows App 配对的播放器地址不同" });
            if (command.Action == "install" && (!_state.Ready || _checked is null || command.CheckId != _state.CheckId))
                return (409, _state with { Detail = "请重新检查后安装" });
            var connection = _connection;
            var manifest = command.Action == "install" ? _checked : null;
            _state = _state with { RequestId = command.RequestId, PlayerUrl = connection.Url, PlayerId=connection.DeviceId,
                Busy = true, Ready = false, Percent = 0, Phase = command.Action == "check" ? "checking" : "uploading",
                Detail = command.Action == "check" ? "Windows App 正在检查播放器" : "Windows App 正在准备推送" };
            _job = Task.Run(() => RunAsync(connection, manifest, _stop.Token));
            return (202, _state);
        }
    }

    private void Publish(Func<HarpUpdateState, HarpUpdateState> change)
    {
        lock (_gate) _state = change(_state);
        Changed?.Invoke();
    }

    private async Task RunAsync(HarpPlayerSettings connection, DeviceFirmwareManifest? install, CancellationToken ct)
    {
        try
        {
            Publish(s => s);
            if (!string.IsNullOrEmpty(connection.DeviceId))
            {
                var found = (await HarpDiscoveryClient.ScanAsync(ct)).FirstOrDefault(d => d.DeviceId == connection.DeviceId);
                if (found is null) throw new InvalidOperationException("未扫描到已配对的 Harp,请确认同一局域网");
                connection = connection with { Url=found.Url };
                lock (_gate) _connection = connection;
                Publish(s => s with {PlayerUrl=connection.Url});
            }
            if (install is null)
            {
                var manifest = _store.Current ?? throw new InvalidOperationException("请在 Windows App 导入 Harp 固件");
                var player = await ReadAsync(connection, ct);
                Compatible(player, manifest);
                lock (_gate) _checked = manifest;
                Publish(s => s with { Phase = "ready", Busy = false, Ready = true, CheckId = Guid.NewGuid().ToString("N"),
                    Current = player.Version, Version = manifest.FirmwareVersion,
                    Detail = player.Version == manifest.FirmwareVersion ? "已是当前版本,可重新安装" : "发现新版本,可安装" });
            }
            else await InstallAsync(connection, install, ct);
        }
        catch (Exception ex)
        {
            // Never surface response bodies, URLs with credentials, or HTTP exception details.
            string detail = ex is InvalidOperationException ? ex.Message : ex is OperationCanceledException
                ? "更新已中断,请重新检查" : "无法连接播放器或响应无效,请重新检查";
            Publish(s => s with { Phase = "error", Busy = false, Ready = false, Detail = detail });
        }
    }

    private sealed record Player
    {
        public bool Ok { get; init; }
        public string Version { get; init; } = "";
        public string Board { get; init; } = "";
        public string Layout { get; init; } = "";
        public string Partition { get; init; } = "";
        public string Phase { get; init; } = "";
        public string UpdateId { get; init; } = "";
        public int MaxBytes { get; init; }
        public int ChunkBytes { get; init; }
        public int Received { get; init; }
        public string Error { get; init; } = "";
    }
    private async Task<(int Code, Player Body)> RequestAsync(HarpPlayerSettings c, string suffix, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post,c.Url + "/api/v1/firmware" + suffix);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", c.ApiKey);
        request.Headers.ConnectionClose = true;
        // ESP32 WebServer expects Content-Length; JsonContent otherwise sends chunked requests.
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body,Json),Encoding.UTF8,"application/json");
        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headersTimeout.CancelAfter(TimeSpan.FromSeconds(suffix=="/begin" ? 30 : 8));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
        // Firmware responses are small. Bound body size and read time independently of HTTP headers.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        byte[] bytes = new byte[4097]; int count = 0;
        while (count < bytes.Length)
        {
            int n = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
            if (n == 0) break;
            count += n;
        }
        if (count == bytes.Length) throw new IOException("Response too large");
        var result = JsonSerializer.Deserialize<Player>(bytes.AsSpan(0,count), Json) ?? throw new IOException("Empty response");
        return ((int)response.StatusCode, result);
    }
    private static void Require((int Code, Player Body) reply)
    {
        if (reply.Code == 200 && reply.Body.Ok) return;
        throw new InvalidOperationException(reply.Code == 401 ? "播放器配对密钥错误" : reply.Code == 404 ? "播放器需先通过 USB 安装 v2"
            : reply.Body.Error == "eject_sd_on_computer_first" ? "旧版播放器需先升级 v3 才支持自动准备更新"
            : reply.Body.Error == "sd_io_busy" ? "SD 持续读写,请等待复制完成后重试" : "播放器拒绝更新,请重新检查");
    }
    private async Task<Player> ReadAsync(HarpPlayerSettings c, CancellationToken ct)
    {
        var reply = await RequestAsync(c, "", null, ct); Require(reply); return reply.Body;
    }
    private static void Compatible(Player p, DeviceFirmwareManifest m)
    {
        m.Validate();
        const string prefix = "delta-harp-v";
        if (m.Board != Board || p.Board != Board || p.Layout != Layout || !p.Version.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(p.Version[prefix.Length..], out int rev) || rev < 2 || p.Version != prefix + rev
            || m.Revision < rev || p.MaxBytes < m.Images[0].Size || p.ChunkBytes < 4096 || p.Partition is not ("app0" or "app1"))
            throw new InvalidOperationException("固件不兼容或版本低于播放器");
    }

    private async Task InstallAsync(HarpPlayerSettings c, DeviceFirmwareManifest manifest, CancellationToken ct)
    {
        var player = await ReadAsync(c, ct); Compatible(player, manifest);
        var image = manifest.Images.Single();
        using var stream = _store.OpenImage(manifest.BundleId, "player") ?? throw new InvalidOperationException("固件文件不可用,请重新导入");
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (hash != image.Sha256) throw new InvalidOperationException("固件校验失败,请重新导入");
        stream.Position = 0;
        string session = ""; bool commitMayHaveSucceeded = false;
        try
        {
            var begin = await RequestAsync(c, "/begin", new { board = Board, layout = Layout, size = image.Size, sha256 = image.Sha256 }, ct);
            for(int attempt=0;begin.Code==409 && begin.Body.Error=="sd_io_busy" && attempt<12;++attempt)
            {
                Publish(s=>s with {Detail="等待 USB S3 完成 SD 读写并准备更新"});
                await Task.Delay(500,ct);
                begin=await RequestAsync(c,"/begin",new {board=Board,layout=Layout,size=image.Size,sha256=image.Sha256},ct);
            }
            Require(begin); session = begin.Body.UpdateId;
            if (session.Length is < 1 or > 64) throw new InvalidOperationException("更新启动结果未知,30秒后重新检查");
            byte[] bytes = new byte[4096];
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (int offset = 0; offset < image.Size;)
            {
                int n = Math.Min(bytes.Length, image.Size-offset);
                await stream.ReadExactlyAsync(bytes.AsMemory(0,n), ct); digest.AppendData(bytes,0,n);
                var body = new { updateId = session, offset, data = Convert.ToBase64String(bytes,0,n) };
                bool accepted = false;
                for (int attempt=0; attempt<2 && !accepted; ++attempt)
                {
                    try
                    {
                        var reply = await RequestAsync(c, "/chunk", body, ct);
                        accepted = reply.Code == 200 && reply.Body.Ok && reply.Body.UpdateId == session && reply.Body.Received == offset+n;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }
                    if (!accepted)
                    {
                        var status = await ReadAsync(c,ct);
                        if (status.UpdateId != session) throw new InvalidOperationException("更新会话已失效,请重新检查");
                        accepted = status.Received == offset+n;
                        if (!accepted && status.Received != offset) throw new InvalidOperationException("播放器更新进度异常");
                    }
                }
                if (!accepted) throw new InvalidOperationException("固件上传中断,请重新检查");
                offset += n;
                Publish(s => s with { Percent = (int)((long)offset*100/image.Size), Detail = "Windows App 正在直传 USB 播放器" });
            }
            if (Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant() != image.Sha256)
                throw new InvalidOperationException("传输中固件校验失败");
            Publish(s => s with { Phase = "restarting", Detail = "推送完成,等待播放器重启" });
            // Commit once only. A lost reply may already have selected the new boot slot.
            commitMayHaveSucceeded = true;
            try
            {
                var commit = await RequestAsync(c, "/commit", new { updateId = session }, ct);
                if (commit.Code != 200 || !commit.Body.Ok) { commitMayHaveSucceeded = false; Require(commit); }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (timer.Elapsed < _rebootTimeout)
            {
                await Task.Delay(500,ct);
                try
                {
                    var status = await ReadAsync(c,ct);
                    if (status.Phase == "idle" && status.Board == Board && status.Layout == Layout && status.Version == manifest.FirmwareVersion
                        && status.Partition == (player.Partition == "app0" ? "app1" : "app0"))
                    {
                        Publish(s => s with { Phase = "complete", Busy = false, Ready = false, Current = manifest.FirmwareVersion,
                            Detail = "更新完成,播放器已重启", Percent = 100 });
                        return;
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
            }
            throw new InvalidOperationException("重启结果未确认,请重新检查");
        }
        finally
        {
            if (session.Length > 0 && !commitMayHaveSucceeded)
            {
                using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await RequestAsync(c, "/abort", new { updateId = session }, abort.Token); } catch (Exception) { }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _stop.Cancel(); }
        _ = _job.ContinueWith(_ => { _http.Dispose(); _stop.Dispose(); }, TaskScheduler.Default);
    }
}
