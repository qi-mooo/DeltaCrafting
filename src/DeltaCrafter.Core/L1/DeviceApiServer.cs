using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaCrafter.Core.L0;
using Serilog;

namespace DeltaCrafter.Core.L1;

/// <summary>HTTP 传输边界;不接触游戏、设置或 UI。监听生命周期由设备编排器管理。</summary>
public sealed class DeviceApiServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(8);
    private readonly Func<CancellationToken, Task<DeviceStatus>> _status;
    private readonly Func<DeviceActionRequest, CancellationToken, Task<DeviceActionResult>> _action;
    private readonly Func<DeviceSettingsRequest, CancellationToken, Task<DeviceActionResult>>? _settings;
    private readonly Func<FacilityKey, CancellationToken, Task<DeviceItemList>>? _items;
    private readonly ILogger _log;
    private readonly byte[] _keyHash;
    private readonly bool _allowControl;
    private Task? _loop;
    private int _disposed;
    public event Action<Exception>? Failed;

    public DeviceApiServer(DeviceApiSettings settings,
        Func<CancellationToken, Task<DeviceStatus>> status,
        Func<DeviceActionRequest, CancellationToken, Task<DeviceActionResult>> action, ILogger log,
        Func<DeviceSettingsRequest, CancellationToken, Task<DeviceActionResult>>? updateSettings = null,
        Func<FacilityKey, CancellationToken, Task<DeviceItemList>>? getItems = null)
    {
        settings.Validate();
        _status = status;
        _action = action;
        _settings = updateSettings;
        _items = getItems;
        _log = log;
        _allowControl = settings.AllowControl;
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey));
        // 应用本身以管理员身份运行;无需额外 URL ACL,不自动修改防火墙。
        _listener.Prefixes.Add($"http://+:{settings.Port}/api/v1/");
        _listener.IgnoreWriteExceptions = true;
    }

    public void Start()
    {
        _listener.Start(); // 端口冲突/权限错误同步返回设置页。
        _loop = ListenAsync();
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                if (!_slots.Wait(0))
                {
                    context.Response.StatusCode = 503;
                    context.Response.Close();
                    continue;
                }
                _ = HandleAsync(context);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Error(ex, "设备 API 监听已停止。");
            _stop.Cancel();
            _listener.Close();
            Failed?.Invoke(ex);
        }
    }

    private bool Authorized(string? authorization)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || authorization.Length > 256) return false;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[prefix.Length..]));
        return CryptographicOperations.FixedTimeEquals(hash, _keyHash);
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = timeout.Token;
        try
        {
            var request = context.Request;
            if (!Authorized(request.Headers["Authorization"]))
            {
                context.Response.Headers["WWW-Authenticate"] = "Bearer";
                await ReplyAsync(context, 401, new { error = "unauthorized" }, ct);
                return;
            }
            string? path = request.Url?.AbsolutePath;
            string? method = path switch
            {
                "/api/v1/status" => "GET",
                "/api/v1/items" => "GET",
                "/api/v1/action" => "POST",
                "/api/v1/settings" => "POST",
                _ => null,
            };
            if (method is null)
                await ReplyAsync(context, 404, new { error = "not_found" }, ct);
            else if (request.HttpMethod != method)
            {
                context.Response.Headers["Allow"] = method;
                await ReplyAsync(context, 405, new { error = "method_not_allowed" }, ct);
            }
            else if (path == "/api/v1/items")
            {
                string? facility = request.QueryString["facility"];
                var keys = FacilityKeys.All.Where(k => FacilityKeys.JsonKey(k) == facility).ToArray();
                if (keys.Length != 1)
                    await ReplyAsync(context, 400, new { error = "invalid_facility" }, ct);
                else if (_items is null)
                    await ReplyAsync(context, 501, new { error = "items_not_supported" }, ct);
                else await ReplyAsync(context, 200, await _items(keys[0], ct).WaitAsync(ct), ct);
            }
            else if (method == "GET")
                await ReplyAsync(context, 200, await _status(ct).WaitAsync(ct), ct);
            else if (!_allowControl)
                await ReplyAsync(context, 403, new { error = "control_disabled" }, ct);
            else if (!string.Equals(request.ContentType?.Split(';')[0].Trim(),
                         "application/json", StringComparison.OrdinalIgnoreCase))
                await ReplyAsync(context, 415, new { error = "application_json_required" }, ct);
            else
            {
                // 包含 chunked 的请求同样限长,不信任 Content-Length。
                byte[] body = new byte[1025];
                int count = 0;
                while (count < body.Length)
                {
                    int read = await request.InputStream.ReadAsync(body.AsMemory(count), ct).AsTask().WaitAsync(ct);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 1024)
                {
                    await ReplyAsync(context, 413, new { error = "body_too_large" }, ct);
                    return;
                }
                if (path == "/api/v1/settings")
                {
                    var update = JsonSerializer.Deserialize<DeviceSettingsRequest>(body.AsSpan(0, count), Json);
                    if (update is null || !update.IsValid())
                    {
                        await ReplyAsync(context, 400, new { error = "invalid_settings" }, ct);
                        return;
                    }
                    var saved = _settings is null ? new DeviceActionResult(501, "settings_not_supported")
                        : await _settings(update, ct).WaitAsync(ct);
                    await ReplyAsync(context, saved.StatusCode, new { message = saved.Message }, ct);
                    return;
                }
                var command = JsonSerializer.Deserialize<DeviceActionRequest>(body.AsSpan(0, count), Json);
                if (command is null || !command.IsValid())
                {
                    await ReplyAsync(context, 400, new { error = "invalid_action" }, ct);
                    return;
                }
                var result = await _action(command, ct).WaitAsync(ct);
                await ReplyAsync(context, result.StatusCode, new { message = result.Message }, ct);
            }
        }
        catch (JsonException)
        {
            await TryErrorAsync(context, 400, "invalid_json");
        }
        catch (OperationCanceledException)
        {
            await TryErrorAsync(context, 503, "request_cancelled");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "设备 API 请求失败。");
            await TryErrorAsync(context, 500, "internal_error");
        }
        finally
        {
            try { context.Response.Close(); }
            finally { _slots.Release(); }
        }
    }

    private static async Task TryErrorAsync(HttpListenerContext context, int code, string error)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await ReplyAsync(context, code, new { error }, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or
                                      ObjectDisposedException or OperationCanceledException) { }
    }

    private static async Task ReplyAsync(HttpListenerContext context, int code, object value, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var response = context.Response;
        response.StatusCode = code;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.KeepAlive = false;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, ct).AsTask().WaitAsync(ct);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // 取消后 UI 队列中的请求不能再执行;无需阻塞 UI 等待后台请求结束。
        _stop.Cancel();
        _listener.Close();
        _ = ReleaseResourcesAsync();
    }

    private async Task ReleaseResourcesAsync()
    {
        if (_loop is not null) await _loop.ConfigureAwait(false);
        // 先等接收循环退出,再收齐处理槽位,避免与请求的 finally/取消回调争用。
        for (int i = 0; i < 8; i++) await _slots.WaitAsync().ConfigureAwait(false);
        _slots.Dispose();
        _stop.Dispose();
    }
}
