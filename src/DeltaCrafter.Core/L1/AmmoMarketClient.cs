using System.Net;
using System.Text.Json;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

public sealed class AmmoMarketClient(HttpClient? http = null) : IAmmoMarketSource
{
    private static readonly HttpClient Shared = new(new HttpClientHandler
    { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly HttpClient _http = http ?? Shared;

    public async Task<AmmoPriceHistory> FetchHistoryAsync(long objectId, string token, CancellationToken ct)
    {
        if (objectId <= 0) throw new ArgumentOutOfRangeException(nameof(objectId));
        if (token.Length != 32 || !token.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("请先在设置中填写三角洲数据帝 Token。");
        try
        {
            using var response = await _http.GetAsync(
                $"https://orzice.com/workApi/v1/sjz_api/minute?id={objectId}&token={token}", ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"数据帝历史查询失败 (HTTP {(int)response.StatusCode})。");
            return ParseHistory(objectId, await response.Content.ReadAsStringAsync(ct), DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("历史查询超时，请手动重试。"); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("无法连接数据帝，请检查网络。"); }
    }

    public static AmmoPriceHistory ParseHistory(long objectId, string json, DateTimeOffset fetchedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            int code = root.GetProperty("code").GetInt32();
            if (code != 0) throw new ProviderException($"数据帝拒绝历史查询 (代码 {code})，请检查 Token 和额度。");
            var data = root.GetProperty("data");
            var times = data.GetProperty("a").EnumerateArray().Select(t => t.GetString() ?? "").ToArray();
            var prices = data.GetProperty("b").EnumerateArray().Select(p => p.GetDecimal()).ToArray();
            if (objectId <= 0 || times.Length != prices.Length || times.Length > 1440
                || times.Any(t => t.Length is 0 or > 60 || t.Any(char.IsControl))
                || prices.Any(p => p is < 0 or > 1_000_000_000)) throw new FormatException();
            // 零值是缺失报价，不能当作免费子弹参与指标计算。
            return new(objectId, fetchedAt, times.Zip(prices).Where(p => p.Second > 0)
                .Select(p => new AmmoPricePoint(p.First, p.Second)).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or OverflowException
            || ex is InvalidOperationException and not ProviderException)
        { throw new InvalidOperationException("子弹历史价格格式异常。"); }
    }

    public async Task<AmmoForecast> FetchLowPricesAsync(int grade, string token, CancellationToken ct)
    {
        if (grade is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(grade));
        if (token.Length != 32 || !token.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("请先在设置中填写三角洲数据帝 Token。");
        try
        {
            using var response = await _http.GetAsync(
                $"https://orzice.com/workApi/v1/sjz_api/ammo_day?grade={grade}&token={token}", ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"数据帝子弹查询失败 (HTTP {(int)response.StatusCode})。");
            return Parse(grade, await response.Content.ReadAsStringAsync(ct), DateTimeOffset.Now);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("子弹查询超时，请手动重试。"); }
        catch (HttpRequestException)
        { throw new InvalidOperationException("无法连接数据帝，请检查网络。"); }
    }

    public static AmmoForecast Parse(int grade, string json, DateTimeOffset fetchedAt)
    {
        if (grade is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(grade));
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            int code = root.GetProperty("code").GetInt32();
            if (code != 0) throw new ProviderException($"数据帝拒绝子弹查询 (代码 {code})，请检查 Token 和额度。");
            var rows = new List<AmmoLowPrice>();
            var hours = new HashSet<int>();
            var identities = new Dictionary<long, (long ObjectId, string Name, int Grade)>();
            foreach (var group in root.GetProperty("data").EnumerateArray())
            {
                int hour = group.GetProperty("hour").GetInt32();
                if (hour is < 0 or > 23 || !hours.Add(hour)) throw new FormatException();
                var seen = new HashSet<long>();
                foreach (var item in group.GetProperty("data").EnumerateArray())
                {
                    long id = item.GetProperty("id").GetInt64(), oid = item.GetProperty("oid").GetInt64();
                    int quality = item.GetProperty("grade").GetInt32();
                    decimal price = item.GetProperty("price").GetDecimal();
                    string name = item.GetProperty("name").GetString()?.Trim() ?? "";
                    if (id <= 0 || oid <= 0 || quality != grade || price is <= 0 or > 1_000_000_000
                        || name.Length is 0 or > 160 || name.Any(char.IsControl) || !seen.Add(id)) throw new FormatException();
                    var identity = (oid, name, quality);
                    if (identities.TryGetValue(id, out var previous) && previous != identity) throw new FormatException();
                    identities[id] = identity;
                    string image = item.TryGetProperty("pic", out var pic) && pic.ValueKind == JsonValueKind.String
                        ? SafeImageUrl(pic.GetString() ?? "") : "";
                    rows.Add(new(id, oid, name, quality, hour, price, image));
                    if (rows.Count > 4800) throw new FormatException();
                }
            }
            return new(grade, fetchedAt, AmmoForecast.ChinaDate(fetchedAt), rows.OrderBy(r => r.Hour).ThenBy(r => r.Price).ToArray());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or OverflowException
            || ex is InvalidOperationException and not ProviderException)
        { throw new InvalidOperationException("子弹预测数据格式异常，已保留上次结果。"); }
    }

    private sealed class ProviderException(string message) : InvalidOperationException(message);

    public static string SafeImageUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && ((uri.Host == "playerhub.df.qq.com" && uri.AbsolutePath.StartsWith("/playerhub/", StringComparison.Ordinal))
            || (uri.Host == "orzice.com" && uri.AbsolutePath.StartsWith("/update/sjz_pic/", StringComparison.Ordinal)))
        ? uri.AbsoluteUri : "";
}
