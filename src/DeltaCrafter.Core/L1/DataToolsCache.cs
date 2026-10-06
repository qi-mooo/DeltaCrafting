using System.Text.Json;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>集市按供应方结束时间缓存到磁盘；仅显式打开工具时检查过期。</summary>
public sealed class DataToolsCache(
    Func<DataToolQuery, string, CancellationToken, Task<DataToolResult>> fetch,
    string marketPath, Func<DateTimeOffset>? now = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private DataToolResult? _market, _weapons;
    private bool _loaded;

    public async Task<DataToolResult> FetchAsync(DataToolQuery query, string token, CancellationToken ct)
    {
        if (!query.IsValid()) throw new ArgumentException("工具查询参数无效。");
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                try { _market = JsonSerializer.Deserialize<DataToolResult>(await File.ReadAllTextAsync(marketPath, ct)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
            if (query.Tool == "market" && _market is { Tool: "market", Entries: not null, ExpiresAt: { } expires }
                && _now() < expires) return _market;
            if (query.Tool == "gun-keys" && _weapons is not null) return _weapons;
            var result = await fetch(query, token, ct);
            if (query.Tool == "market")
            {
                _market = result;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(marketPath)!);
                    await File.WriteAllTextAsync(marketPath + ".tmp", JsonSerializer.Serialize(result), ct);
                    File.Move(marketPath + ".tmp", marketPath, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (query.Tool == "gun-keys") _weapons = result;
            return result;
        }
        finally { _gate.Release(); }
    }
}
