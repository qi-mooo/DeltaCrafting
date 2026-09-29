namespace DeltaCrafter.Core.L0;

public sealed class ManufactureApiSettings
{
    public string Token { get; set; } = "";
    public Dictionary<string, int> FacilityLevels { get; set; } = [];
    public int LevelFor(FacilityKey key) => FacilityLevels.TryGetValue(FacilityKeys.JsonKey(key), out int level)
        ? Math.Clamp(level, 1, 3) : 3;
}

public sealed record ManufactureItem(long ObjectId, string Name, int Grade, int UnlockLevel,
    double PeriodHours, double Profit, double HourlyProfit)
{
    public CatalogItem ToCatalogItem() => new()
    {
        ObjectId = ObjectId, Name = Name, Grade = Grade, UnlockLevel = UnlockLevel,
    };
}

public sealed record ManufactureMarketSnapshot(FacilityKey Facility, int Level,
    DateTimeOffset FetchedAt, IReadOnlyList<ManufactureItem> Items)
{
    public ProfitRecommendation Best(CraftMode mode)
    {
        if (mode == CraftMode.Custom) throw new InvalidOperationException("自定义模式没有自动推荐。");
        var best = Items.Where(i => i.UnlockLevel <= Level)
            .OrderByDescending(i => mode == CraftMode.HourlyProfit ? i.HourlyProfit : i.Profit)
            .ThenBy(i => i.ObjectId).FirstOrDefault()
            ?? throw new InvalidOperationException("当前设施等级没有可用配方。");
        return new(Facility, best.Name, mode == CraftMode.HourlyProfit ? best.HourlyProfit : best.Profit);
    }
}

public static class ManufactureCatalog
{
    // Build all four facilities before the caller replaces the on-disk catalog.
    // Only verified built-in names supplement the API; OCR scan guesses are not carried forward.
    public static ItemCatalog Build(IReadOnlyList<ManufactureMarketSnapshot> snapshots, ItemCatalog builtIn)
    {
        if (snapshots.Count != 4 || snapshots.Select(s => s.Facility).Distinct().Count() != 4
            || FacilityKeys.All.Any(key => !snapshots.Any(s => s.Facility == key && s.Items.Count > 0)))
            throw new InvalidOperationException("四设施配方不完整,已保留原目录。");
        var catalog = new ItemCatalog
        {
            Revision = builtIn.Revision, Source = "三角洲数据帝 + 内置确认配方",
            UpdatedAt = snapshots.Max(s => s.FetchedAt),
        };
        foreach (var snapshot in snapshots)
        {
            var rows = snapshot.Items.Select(i => i.ToCatalogItem()).ToList();
            var names = rows.Select(i => Identity(i.Name)).ToHashSet(StringComparer.Ordinal);
            rows.AddRange(builtIn.For(snapshot.Facility).Where(i => names.Add(Identity(i.Name))));
            catalog.Facilities[FacilityKeys.JsonKey(snapshot.Facility)] = rows;
        }
        return catalog;
    }

    private static string Identity(string name) => CatalogNameResolver.Canonical(
        BlkAmmoIdentity.Grade(name) is { } grade ? BlkAmmoIdentity.Name(grade) : name);
}
