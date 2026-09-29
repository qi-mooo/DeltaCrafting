using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>具体工具打开时单次查询；不预取、重试或定时刷新，不输出含 Token 的异常。</summary>
public sealed class DataToolsClient
{
    private static readonly HttpClient Shared = new(new HttpClientHandler
    { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly HttpClient _http;
    public DataToolsClient(HttpClient? http = null) => _http = http ?? Shared;

    public async Task<DataToolResult> FetchAsync(DataToolQuery query, string token, CancellationToken ct)
    {
        if (!query.IsValid()) throw new ArgumentException("工具查询参数无效。");
        if (!Regex.IsMatch(token, "\\A[a-zA-Z0-9]{32}\\z"))
            throw new InvalidOperationException("请先在设置中填写三角洲数据帝 Token。");
        string path = query.Tool switch { "password" => "map_pwd", "market" => "market", _ => "gun_gqm_v2" };
        string parameters = query.Tool == "gun"
            ? $"&p={query.Page}&solutionType={query.Mode}&key1={Uri.EscapeDataString("全部")}&key2={Uri.EscapeDataString("全部")}&top=0&top2=3&n={Uri.EscapeDataString(query.Search)}" : "";
        try
        {
            using var response = await _http.GetAsync($"https://orzice.com/workApi/v1/sjz_api/{path}?token={token}{parameters}", ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"数据帝请求失败(HTTP {(int)response.StatusCode})。");
            return Parse(query, await response.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("数据帝请求超时，请手动重试。"); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("无法连接三角洲数据帝，请检查网络。"); }
    }

    public static DataToolResult Parse(DataToolQuery query, string json)
    {
        if (!query.IsValid()) throw new ArgumentException("工具查询参数无效。");
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            int code = root.GetProperty("code").GetInt32();
            if (code != 0) throw new ProviderException($"数据帝拒绝请求(代码 {code})，请检查 Token 和额度。");
            var data = root.GetProperty("data");
            var entries = new List<DataToolEntry>();
            bool next = false;
            string detail;
            if (query.Tool == "password")
            {
                string[] maps = ["零号大坝", "长弓溪谷", "巴克什", "航天基地", "潮汐监狱", "AZ3"];
                for (int i = 0; i < maps.Length; i++)
                {
                    string key = ((char)('a' + i)).ToString();
                    if (!data.TryGetProperty(key, out var values))
                    {
                        entries.Add(new(key, maps[i], ["暂未更新"]));
                        continue;
                    }
                    string password = Text(values[0], 16), date = Text(values[1], 40);
                    entries.Add(new(key, maps[i], [$"密码：{(password == "-" ? "暂未更新" : password)}", $"日期：{date}"]));
                }
                detail = "以各地图标注的日期为准";
            }
            else if (query.Tool == "market")
            {
                detail = $"{Text(data.GetProperty("begin_date"), 40)} 至 {Text(data.GetProperty("end_date"), 40)}";
                foreach (var row in data.GetProperty("items").EnumerateArray())
                {
                    int grade = row.GetProperty("grade").GetInt32();
                    if (grade is < 0 or > 6) throw new FormatException();
                    string name = Text(row.GetProperty("name"));
                    var lines = new List<string> { detail, "当前集市所需物品" };
                    if (row.TryGetProperty("price", out var price) && price.TryGetInt64(out long amount) && amount >= 0)
                        lines.Add($"当前价格：{amount:N0}");
                    entries.Add(new(row.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                        CatalogItemLabel.Format(new() { Name = name, Grade = grade }), lines));
                }
            }
            else
            {
                foreach (var row in data.EnumerateArray())
                {
                    string name = Text(row.GetProperty("name")), gun = Text(row.GetProperty("objectName"));
                    string copy = Text(row.GetProperty("solutionCode"), 256);
                    string mode = Text(row.GetProperty("solutionType"), 16) == "operator" ? "大战场" : "烽火地带";
                    double price = row.GetProperty("price").GetDouble();
                    if (!double.IsFinite(price) || price < 0) throw new FormatException();
                    entries.Add(new(row.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture),
                        name, [gun + " · " + mode, $"价格：{price:N0}",
                        "作者：" + (row.TryGetProperty("authorNickname", out var author) && author.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(author.GetString()) ? Text(author) : "未署名"), "改枪码：" + copy], copy));
                }
                // V2 文档示例和实测均为每页 12 条；末页不能用末页条数反推已浏览数量。
                int count = root.GetProperty("count").GetInt32();
                if (entries.Count > 12 || count < 0) throw new FormatException();
                next = entries.Count > 0 && (long)(query.Page - 1) * 12 + entries.Count < count;
                detail = $"{(query.Mode == "operator" ? "大战场" : "烽火地带")} · 第 {query.Page} 页 · 共 {count} 项";
            }
            if (entries.Count > 200) throw new FormatException();
            return new(query.Tool, query.Title, query.Page, next, detail, entries, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or OverflowException
            or IndexOutOfRangeException || ex is InvalidOperationException and not ProviderException)
        { throw new InvalidOperationException("数据帝工具数据格式异常，请稍后重试。"); }
    }

    private static string Text(JsonElement value, int limit = 160)
    {
        string text = value.GetString()?.Trim() ?? "";
        if (text.Length == 0 || text.Length > limit || text.Any(char.IsControl)) throw new FormatException();
        return text;
    }
    private sealed class ProviderException(string message) : InvalidOperationException(message);
}
