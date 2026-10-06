using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>基础物品数据只在手动刷新目录时请求，无启动请求和自动重试。</summary>
public sealed class ItemMetadataApiClient
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
    }) { Timeout = TimeSpan.FromSeconds(40), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    private readonly HttpClient _http;
    public ItemMetadataApiClient(HttpClient? http = null) => _http = http ?? SharedClient;

    public async Task<ItemMetadataSnapshot> FetchAsync(string token, CancellationToken ct)
    {
        if (!Regex.IsMatch(token, "\\A[a-zA-Z0-9]{32}\\z"))
            throw new InvalidOperationException("请在设置页填写有效的三角洲数据帝 Token。");
        try
        {
            using var response = await _http.GetAsync(
                $"https://orzice.com/workApi/v1/sjz_api/item_info_all?token={token}", ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"基础物品数据请求失败(HTTP {(int)response.StatusCode})。");
            return new(DateTimeOffset.Now, Parse(await response.Content.ReadAsStringAsync(ct)));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("基础物品数据请求超时。");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("无法连接三角洲数据帝,请检查网络。");
        }
    }

    public static IReadOnlyList<ItemMetadata> Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int code = root.GetProperty("code").GetInt32();
            if (code != 0) throw new MetadataApiException($"数据帝拒绝请求(代码 {code}),请检查 Token 和剩余额度。");
            var rows = root.GetProperty("data");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 10000)
                throw new FormatException();
            var result = new HashSet<ItemMetadata>();
            foreach (var row in rows.EnumerateArray())
            {
                long id = row.GetProperty("objectID").GetInt64();
                string name = row.GetProperty("objectName").GetString()?.Trim() ?? "";
                int grade = row.GetProperty("grade").GetInt32();
                if (id < 0 || name.Length is < 2 or > 120 || name.Any(char.IsControl) || grade is < 0 or > 7)
                    throw new FormatException();
                // 磨损装备等无官方 ID 的变体不参与制造物品匹配。
                if (id > 0) result.Add(new(id, name, grade));
            }
            if (result.Count == 0) throw new FormatException();
            return result.ToArray();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or OverflowException
            || ex is InvalidOperationException and not MetadataApiException)
        {
            throw new InvalidOperationException("基础物品数据不完整,已保留原目录和计划。");
        }
    }

    private sealed class MetadataApiException(string message) : InvalidOperationException(message);
}
