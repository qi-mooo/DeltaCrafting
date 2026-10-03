namespace DeltaCrafter.Core.L0;

/// <summary>进度使用接口制造周期，缺失时回退到已确认开工的时长；不推断完成时间。</summary>
public static class CraftProgress
{
    public static long? SecondsFromHours(double? hours) => hours is > 0 and <= 8760
        && double.IsFinite(hours.Value) ? (long)Math.Ceiling(hours.Value * 3600) : null;

    public static long? TotalSeconds(FacilityRuntime runtime)
    {
        if (runtime.Phase != FacilityPhase.Crafting) return null;
        if (runtime.RecipeTotalSeconds is > 0 and <= 31536000) return runtime.RecipeTotalSeconds;
        return runtime.StartedAt is { } start && runtime.ReadyAt is { } end && end > start
            ? (long)Math.Ceiling((end - start).TotalSeconds) : null;
    }

    public static double? Percent(FacilityPhase phase, DateTimeOffset? readyAt, long? totalSeconds, DateTimeOffset now)
    {
        if (phase == FacilityPhase.ReadyToCollect) return 100;
        if (phase != FacilityPhase.Crafting || readyAt is not { } end) return null;
        if (end <= now) return 100;
        if (totalSeconds is not > 0) return null;
        return Math.Clamp(100 * (1 - (end - now).TotalSeconds / totalSeconds.Value), 0, 100);
    }
}

public static class ManufacturePeriods
{
    /// <summary>普通接口只补 Pro 缺项；确认重叠配方周期全部一致后才应用于三级设施。</summary>
    public static int SupplementFromLegacy(ItemCatalog catalog, ManufactureMarketSnapshot pro, IReadOnlyList<CatalogItem> legacy)
    {
        if (pro.Level != 3 || pro.Items.Count == 0) return 0;
        // 普通接口没有等级参数，用同设施 Pro 读数校验基准，禁止推测等级换算。
        foreach (var item in pro.Items)
        {
            var matches = legacy.Where(i => i.ObjectId == item.ObjectId).Take(2).ToArray();
            if (matches.Length != 1 || CraftProgress.SecondsFromHours(matches[0].PeriodHours)
                != CraftProgress.SecondsFromHours(item.PeriodHours)) return 0;
        }
        var proIds = pro.Items.Select(i => i.ObjectId).ToHashSet();
        var periods = new ItemCatalog { Facilities = new()
        {
            [FacilityKeys.JsonKey(pro.Facility)] = legacy.Where(i => !proIds.Contains(i.ObjectId)).ToList(),
        } };
        return Merge(catalog, periods, overwrite: false);
    }

    public static long? SecondsFor(IReadOnlyList<CatalogItem> items, string name, int facilityLevel)
    {
        if (string.IsNullOrWhiteSpace(name) || facilityLevel is < 1 or > 3) return null;
        var matches = items.Where(i => Identity(i.Name) == Identity(name)).Take(2).ToArray();
        return matches.Length == 1 && matches[0].PeriodFacilityLevel == facilityLevel
            ? CraftProgress.SecondsFromHours(matches[0].PeriodHours) : null;
    }

    /// <summary>只合并已收录配方的时长，启动种子数据仅补缺项；接口刷新可以覆盖旧周期。</summary>
    public static int Merge(ItemCatalog catalog, ItemCatalog periods, bool overwrite)
    {
        int changed = 0;
        foreach (var key in FacilityKeys.All)
        {
            var rows = periods.For(key);
            foreach (var item in catalog.For(key))
            {
                if (!overwrite && CraftProgress.SecondsFromHours(item.PeriodHours) is not null
                    && item.PeriodFacilityLevel is >= 1 and <= 3) continue;
                var matches = rows.Where(r => item.ObjectId > 0 ? r.ObjectId == item.ObjectId
                    : Identity(r.Name) == Identity(item.Name)).Take(2).ToArray();
                if (matches.Length != 1) continue;
                var source = matches[0];
                if (CraftProgress.SecondsFromHours(source.PeriodHours) is null
                    || source.PeriodFacilityLevel is < 1 or > 3) continue;
                if (item.PeriodHours == source.PeriodHours && item.PeriodFacilityLevel == source.PeriodFacilityLevel) continue;
                item.PeriodHours = source.PeriodHours;
                item.PeriodFacilityLevel = source.PeriodFacilityLevel;
                changed++;
            }
        }
        return changed;
    }

    private static string Identity(string name) => CatalogNameResolver.Canonical(
        BlkAmmoIdentity.Grade(name) is { } grade ? BlkAmmoIdentity.Name(grade) : name);
}
