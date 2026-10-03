namespace DeltaCrafter.Core.L0;

public sealed record ItemMetadata(long ObjectId, string Name, int Grade);

public sealed record ItemMetadataSnapshot(DateTimeOffset FetchedAt, IReadOnlyList<ItemMetadata> Items);

/// <summary>基础物品接口只更新目录元数据，不改变配方归属、名称或计划。</summary>
public static class ItemMetadataCatalog
{
    public static int RemoveRetiredLegacyAmmo(ItemCatalog catalog)
    {
        if (!catalog.Facilities.TryGetValue("workbench", out var items)) return 0;
        // 仅清理早期内置目录带入的旧赛季条目；接口有 ID 的新配方仍以接口为准。
        return items.RemoveAll(i => i.ObjectId == 0 && CatalogNameResolver.Canonical(i.Name)
            is "762X39MMSUB-4" or "762X39MMSUB-5");
    }

    public static int Apply(ItemCatalog catalog, ItemMetadataSnapshot snapshot)
    {
        var records = snapshot.Items.Where(i => i.ObjectId > 0 && i.Grade is >= 0 and <= 7
            && !string.IsNullOrWhiteSpace(i.Name)).Distinct().ToArray();
        var byId = records.ToLookup(i => i.ObjectId);
        var byName = records.ToLookup(i => CatalogNameResolver.Canonical(i.Name), StringComparer.Ordinal);
        int changed = 0;
        foreach (var item in catalog.Facilities.Values.SelectMany(rows => rows))
        {
            // 已有 ID 时不按相似名称回退；无 ID 的旧条目必须完整、唯一匹配。
            var matches = (item.ObjectId > 0 ? byId[item.ObjectId]
                : byName[CatalogNameResolver.Canonical(item.Name)]).Take(2).ToArray();
            if (matches.Length != 1) continue;
            var metadata = matches[0];
            if (BlkAmmoIdentity.Grade(item.Name) is { } blkGrade && metadata.Grade != blkGrade) continue;
            bool updateId = item.ObjectId == 0;
            bool updateGrade = metadata.Grade > 0 && item.Grade != metadata.Grade;
            if (!updateId && !updateGrade) continue;
            if (updateId) item.ObjectId = metadata.ObjectId;
            if (updateGrade) item.Grade = metadata.Grade;
            changed++;
        }
        return changed;
    }
}
