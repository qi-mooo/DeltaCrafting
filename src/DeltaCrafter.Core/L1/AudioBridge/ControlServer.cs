using System.Net;
using System.Text.Json;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1.AudioBridge;

/// <summary>Compatibility endpoint for the existing S3 audio firmware.</summary>
public sealed class ControlServer(IWindowsAudio audio, Func<object> health, int port = 8765) : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    public Task Completion => _loop ?? Task.CompletedTask;

    public void Start()
    {
        _listener.Prefixes.Add($"http://+:{port}/");
        _listener.IgnoreWriteExceptions = true;
        _listener.Start();
        _loop = ListenAsync();
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                await HandleAsync(context).ConfigureAwait(false);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var request = context.Request;
            string? path = request.Url?.AbsolutePath;
            string? method = path switch
            {
                "/" or "/health" => "GET",
                "/api/mute" or "/api/volume" => request.HttpMethod == "PUT" ? "PUT" : "GET",
                "/api/mute/toggle" or "/api/volume/up" or "/api/volume/down" => "POST",
                _ => null,
            };
            if (method is null) { await Reply(context, 404, new { error = "not_found" }, timeout.Token); return; }
            if (method != request.HttpMethod) { await Reply(context, 405, new { error = "method_not_allowed" }, timeout.Token); return; }
            JsonDocument? body = null;
            try
            {
                if (method == "PUT")
                {
                    if (request.ContentLength64 > 1024)
                    { await Reply(context, 413, new { error = "body_too_large" }, timeout.Token); return; }
                    // Bound chunked requests too; HttpListener reads need an explicit timeout.
                    var data = new byte[1025];
                    int count = 0;
                    while (count < data.Length)
                    {
                        int read = await request.InputStream.ReadAsync(data.AsMemory(count), timeout.Token)
                            .AsTask().WaitAsync(timeout.Token);
                        if (read == 0) break;
                        count += read;
                    }
                    if (count > 1024)
                    { await Reply(context, 413, new { error = "body_too_large" }, timeout.Token); return; }
                    body = JsonDocument.Parse(data.AsMemory(0, count));
                }
                object result = path switch
                {
                    "/" or "/health" => health(),
                    "/api/mute" when method == "PUT" => audio.SetMute(body!.RootElement.GetProperty("muted").GetBoolean()),
                    "/api/volume" when method == "PUT" => audio.SetVolume(body!.RootElement.GetProperty("volumePercent").GetInt32()),
                    "/api/mute/toggle" => audio.ToggleMute(),
                    "/api/volume/up" => audio.ChangeVolume(5),
                    "/api/volume/down" => audio.ChangeVolume(-5),
                    _ => audio.GetState(),
                };
                await Reply(context, 200, result, timeout.Token);
            }
            finally { body?.Dispose(); }
        }
        catch (AudioDeviceUnavailableException ex) { await TryError(context, 503, ex.Message); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { await TryError(context, 400, "invalid_json"); }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException) { }
        finally { context.Response.Close(); }
    }

    private static async Task TryError(HttpListenerContext context, int status, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await Reply(context, status, new { error = message }, timeout.Token); }
        catch (Exception ex) when (ex is IOException or HttpListenerException or OperationCanceledException or ObjectDisposedException) { }
    }
    private static async Task Reply(HttpListenerContext context, int status, object value, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.KeepAlive = false;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, ct).AsTask().WaitAsync(ct);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        finally { _stop.Dispose(); }
    }
}
