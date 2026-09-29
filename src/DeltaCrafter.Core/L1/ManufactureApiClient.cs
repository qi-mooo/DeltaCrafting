using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>三角洲数据帝制造行情。无重试、无启动请求;调用方决定何时访问。</summary>
public sealed class ManufactureApiClient
{
    private static readonly HttpClient SharedClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All,
    }) { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly HttpClient _http;
    public ManufactureApiClient(HttpClient? http = null) => _http = http ?? SharedClient;

    public async Task<ManufactureMarketSnapshot> FetchAsync(FacilityKey facility, int level,
        string token, CancellationToken ct, bool catalogOnly = false)
    {
        if (!Regex.IsMatch(token, "\\A[a-zA-Z0-9]{32}\\z"))
            throw new InvalidOperationException("请在设置页填写有效的三角洲数据帝 Token。");
        if (level is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(level));
        int type = facility switch
        {
            FacilityKey.TechCenter => 1, FacilityKey.Workbench => 2,
            FacilityKey.PharmacyLab => 3, FacilityKey.ArmorStation => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(facility)),
        };
        try
        {
            using var response = await _http.GetAsync(
                $"https://orzice.com/workApi/v1/sjz_api/manufacturePro?t={type}&l={level}&token={token}", ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"数据帝请求失败(HTTP {(int)response.StatusCode})。");
            var json = await response.Content.ReadAsStringAsync(ct);
            return new(facility, level, DateTimeOffset.Now, Parse(json, catalogOnly));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("数据帝请求超时。");
        }
        catch (HttpRequestException)
        {
            // 请求 URL 含 Token,禁止将异常消息/内部异常传给日志或界面。
            throw new InvalidOperationException("无法连接三角洲数据帝,请检查网络。");
        }
    }

    public static IReadOnlyList<ManufactureItem> Parse(string json, bool catalogOnly = false)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            int code = root.GetProperty("code").GetInt32();
            if (code != 0) throw new ManufactureApiException($"数据帝拒绝请求(代码 {code}),请检查 Token 和剩余额度。");
            var rows = root.GetProperty("data");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 1000)
                throw new FormatException();
            var result = new List<ManufactureItem>();
            var ids = new HashSet<long>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                long id = row.GetProperty("objectID").GetInt64();
                string name = row.GetProperty("name").GetString()?.Trim() ?? "";
                int grade = row.GetProperty("grade").GetInt32();
                int unlock = row.GetProperty("unlockLevel").GetInt32();
                // 目录刷新保存配方身份及制造时长，不读取利润字段。
                double period = row.GetProperty("period").GetDouble();
                double profit = catalogOnly ? 0 : row.GetProperty("price").GetDouble();
                double hourly = catalogOnly ? 0 : row.GetProperty("price_hour").GetDouble();
                if (BlkAmmoIdentity.Grade(name) is { } blkGrade)
                {
                    if (grade != blkGrade) throw new FormatException();
                    name = BlkAmmoIdentity.Name(blkGrade);
                }
                if (id <= 0 || name.Length is < 2 or > 120 || name.Any(char.IsControl) ||
                    grade is < 0 or > 7 || unlock is < 1 or > 3 || period <= 0 || period > 8760 ||
                    !double.IsFinite(period) || !double.IsFinite(profit) || !double.IsFinite(hourly) ||
                    !ids.Add(id) || !names.Add(CatalogNameResolver.Canonical(name))) throw new FormatException();
                result.Add(new(id, name, grade, unlock, period, profit, hourly));
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or OverflowException
            || ex is InvalidOperationException and not ManufactureApiException)
        {
            throw new InvalidOperationException("数据帝返回的配方数据不完整,已保留原目录和计划。");
        }
    }

    private sealed class ManufactureApiException(string message) : InvalidOperationException(message);
}
