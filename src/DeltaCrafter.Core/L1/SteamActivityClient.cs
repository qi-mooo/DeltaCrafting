using System.Globalization;
using System.Text.Json;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

public sealed class SteamActivityClient : ISteamActivitySource
{
    private readonly TimeSpan _timeout;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public SteamActivityClient() : this(TimeSpan.FromSeconds(10), null) { }

    internal SteamActivityClient(TimeSpan timeout, Func<HttpMessageHandler>? handlerFactory)
    {
        _timeout = timeout;
        _handlerFactory = handlerFactory;
    }

    public async Task<SteamActivityResult> CheckAsync(string apiKey, string steamId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        apiKey = apiKey?.Trim() ?? "";
        steamId = steamId?.Trim() ?? "";
        if (apiKey.Length != 32 || !apiKey.All(char.IsAsciiHexDigit))
            return Unavailable("Steam API key 未配置或格式无效");
        if (steamId.Length != 17 || !steamId.All(char.IsAsciiDigit)
            || !ulong.TryParse(steamId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return Unavailable("Steam ID 必须是 17 位 SteamID64");

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
            // Never log this URL or transport exception text: Steam requires the key in the query.
            string url = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/"
                + $"?key={Uri.EscapeDataString(apiKey)}&steamids={Uri.EscapeDataString(steamId)}";
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Unavailable($"Steam 查询失败 (HTTP {(int)response.StatusCode})");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return Parse(json.RootElement, steamId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Unavailable("Steam 查询超时");
        }
        catch (HttpRequestException)
        {
            return Unavailable("Steam 服务暂时无法连接");
        }
        catch (JsonException)
        {
            return Unavailable("Steam 响应格式无效");
        }
    }

    private static SteamActivityResult Parse(JsonElement root, string steamId)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array)
            return Unavailable("Steam 响应缺少玩家状态");
        var matches = players.EnumerateArray().Where(p => Text(p, "steamid") == steamId).ToArray();
        if (matches.Length != 1) return Unavailable("Steam 未返回此账号的状态");
        var player = matches[0];
        string gameId = Text(player, "gameid"), game = Text(player, "gameextrainfo");
        if ((!string.IsNullOrWhiteSpace(gameId) && gameId != "0") || !string.IsNullOrWhiteSpace(game))
        {
            string name = string.Concat(game.Where(c => !char.IsControl(c))).Trim();
            if (name.Length > 80) name = name[..80];
            return new(SteamActivityState.Playing,
                name.Length > 0 ? $"Steam 游戏中: {name}" : "Steam 游戏中");
        }
        if ((player.TryGetProperty("gameid", out var id) && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            || (player.TryGetProperty("gameextrainfo", out var info) && info.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            return Unavailable("Steam 游戏状态格式无效");
        if (!player.TryGetProperty("communityvisibilitystate", out var visibility)
            || visibility.ValueKind != JsonValueKind.Number || !visibility.TryGetInt32(out int state) || state != 3)
            return Unavailable("Steam 资料不可见,请将个人资料和游戏详情设为公开");
        return new(SteamActivityState.NotPlaying, "Steam 未报告正在游戏");
    }

    private static string Text(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field)
        && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";

    private static SteamActivityResult Unavailable(string detail) => new(SteamActivityState.Unavailable, detail);
}
