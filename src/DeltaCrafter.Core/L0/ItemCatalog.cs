namespace DeltaCrafter.Core.L0;

/// <summary>
/// 物品目录条目。Name 用于界面显示(可手工改成正确写法);
/// Ocr 仅保留旧版数据兼容，运行期使用目录真实 Name。
/// </summary>
public sealed class CatalogItem
{
    public long ObjectId { get; set; }
    public int Grade { get; set; }
    public int UnlockLevel { get; set; }
    /// <summary>数据帝 manufacturePro 的制造小时数；仅供显示进度，不参与调度。</summary>
    public double? PeriodHours { get; set; }
    /// <summary>查询该时长时的设施等级，防止套用其它等级的生产周期。</summary>
    public int PeriodFacilityLevel { get; set; }
    public string Name { get; set; } = "";
    public string Ocr { get; set; } = "";
    public string? Note { get; set; }

    /// <summary>运行期在游戏列表里搜索用的名称。</summary>
    public string MatchKey => Name;
}

/// <summary>
/// 可制造物品目录(items.json,键为设施 kebab 名)。用于制造计划候选及运行时完整名称校验。
/// </summary>
public sealed class ItemCatalog
{
    public string Source { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    /// <summary>默认表修订号。程序启动时若默认表比本地副本新,自动备份并替换本地副本。</summary>
    public int Revision { get; set; }
    public Dictionary<string, List<CatalogItem>> Facilities { get; set; } = [];

    public IReadOnlyList<CatalogItem> For(FacilityKey key) =>
        Facilities.TryGetValue(FacilityKeys.JsonKey(key), out var list) ? list : [];
}

/// <summary>离线 OCR 结果只能完整、唯一命中目录；不补型号、不做子串或编辑距离猜测。</summary>
public static class CatalogNameResolver
{
    // 只归一化排版差异，保留型号数字和字母；不把 I/1、O/0 或中文形近字互换。
    public static string Canonical(string name) => string.Concat(name.Normalize(System.Text.NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c) && c is not ('.' or '`' or '·'))
        .Select(c => c switch { '×' => 'X', '‐' or '‑' or '–' or '−' => '-', _ => char.ToUpperInvariant(c) }));

    public static string? Resolve(IReadOnlyList<CatalogItem> items, string ocrName)
    {
        string target = Canonical(ocrName);
        if (target.Length < 3) return null;
        var matches = items.Where(i => Canonical(i.Name) == target).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Name : null;
    }

    public static bool Matches(IReadOnlyList<CatalogItem> items, string observed, string expected) =>
        Resolve(items, observed) is { } name && Canonical(name) == Canonical(expected);
}
