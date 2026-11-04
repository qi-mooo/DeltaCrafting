using System.Text;
using System.Text.Json;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

public sealed record BarkResult(bool Success, string Detail);

/// <summary>
/// Bark 推送(POST {服务器}/push)。地址里含设备 key:不得记录 URL 或传输异常原文,
/// 返回的 Detail 只包含可公开的状态描述。
/// </summary>
public sealed class BarkClient
{
    /// <summary>推送图标:仓库里的应用图标(与 Assets/AppIcon.png 同一文件)。</summary>
    public const string IconUrl =
        "https://raw.githubusercontent.com/qi-mooo/DeltaCrafting/main/src/DeltaCrafter.App/Assets/AppIcon.png";

    /// <summary>Bark App 内置铃声(Finb/Bark 仓库 Sounds 目录)。</summary>
    public static readonly IReadOnlyList<string> Sounds =
    [
        "alarm", "anticipate", "bell", "birdsong", "bloom", "calypso", "chime", "choo", "descent",
        "electronic", "fanfare", "glass", "gotosleep", "healthnotification", "horn", "ladder", "mailsent",
        "minuet", "multiwayinvitation", "newmail", "newsflash", "noir", "paymentsuccess", "shake",
        "sherwoodforest", "silence", "spell", "suspense", "telegraph", "tiptoes", "typewriters", "update",
    ];

    private readonly TimeSpan _timeout;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public BarkClient() : this(TimeSpan.FromSeconds(10), null) { }

    internal BarkClient(TimeSpan timeout, Func<HttpMessageHandler>? handlerFactory)
    {
        _timeout = timeout;
        _handlerFactory = handlerFactory;
    }

    /// <summary>解析 Bark App 复制的地址,如 https://api.day.app/KEY 或带路径前缀的自建服务器。</summary>
    public static bool TryParse(string? url, out Uri endpoint, out string deviceKey)
    {
        endpoint = null!;
        deviceKey = "";
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;
        string key = segments[^1];
        if (key.Length is < 8 or > 128 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        string prefix = string.Concat(segments[..^1].Select(s => "/" + s));
        endpoint = new Uri($"{uri.Scheme}://{uri.Authority}{prefix}/push");
        deviceKey = key;
        return true;
    }

    public async Task<BarkResult> SendAsync(string url, NotificationKind kind, string title, string body,
        string sound, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryParse(url, out var endpoint, out string key))
            return new(false, "Bark 地址无效,请粘贴 Bark App 中的完整推送地址");

        var fields = new Dictionary<string, string>
        {
            ["device_key"] = key,
            ["title"] = title,
            ["body"] = body,
            ["group"] = "DeltaCrafter",
            ["icon"] = IconUrl,
            // 需要人工介入的事件用时效性通知,可突破专注模式。
            ["level"] = BarkSettings.IsUrgent(kind) ? "timeSensitive" : "active",
        };
        if (Sounds.Contains(sound)) fields["sound"] = sound;
        var payload = JsonSerializer.Serialize(fields);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            using var handler = _handlerFactory?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = 65536,
            };
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoint, content, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, $"Bark 推送失败 (HTTP {(int)response.StatusCode})");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int value) && value == 200)
                return new(true, "Bark 推送成功");
            return new(false, "Bark 服务器拒绝了推送,请检查设备 key");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "Bark 推送超时");
        }
        catch (HttpRequestException)
        {
            return new(false, "Bark 服务器暂时无法连接");
        }
        catch (JsonException)
        {
            return new(false, "Bark 响应格式无效");
        }
    }
}
