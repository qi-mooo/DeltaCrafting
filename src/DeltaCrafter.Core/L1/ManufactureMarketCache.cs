using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>同设施行情十分钟内复用,并发请求合并。目录只有显式刷新才替换。</summary>
public sealed class ManufactureMarketCache
{
    private readonly ManufactureApiClient _client;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(FacilityKey, int, string), ManufactureMarketSnapshot> _cache = [];

    public ManufactureMarketCache(ManufactureApiClient client, IClock clock)
    {
        _client = client;
        _clock = clock;
    }

    public async Task<ManufactureMarketSnapshot> GetAsync(FacilityKey key, int level, string token,
        bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var cacheKey = (key, level, token);
            if (!force && _cache.TryGetValue(cacheKey, out var cached) &&
                _clock.Now - cached.FetchedAt < TimeSpan.FromMinutes(10)) return cached;
            var value = await _client.FetchAsync(key, level, token, ct) with { FetchedAt = _clock.Now };
            // 不把历史 Token 无限积存在内存中。
            foreach (var old in _cache.Keys.Where(k => k.Item3 != token).ToArray()) _cache.Remove(old);
            _cache[cacheKey] = value;
            return value;
        }
        finally { _gate.Release(); }
    }
}
