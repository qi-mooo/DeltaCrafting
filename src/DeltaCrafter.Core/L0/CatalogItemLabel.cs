namespace DeltaCrafter.Core.L0;

/// <summary>物品身份和显示标签分离，等级标签不能进入 OCR 匹配名或制造计划。</summary>
public static class CatalogItemLabel
{
    public static string Format(CatalogItem item)
    {
        int? ammoGrade = BlkAmmoIdentity.Grade(item.Name);
        int grade = item.Grade is >= 1 and <= 7 ? item.Grade : ammoGrade ?? 0;
        string name = ammoGrade.HasValue ? ".300BLK" : item.Name;
        return grade > 0 ? $"{grade}级 {name}" : $"等级未知 {name}";
    }

    public static string ForName(IReadOnlyList<CatalogItem> items, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var item = items.FirstOrDefault(i => i.Name == name)
            ?? new CatalogItem { Name = name };
        return Format(item);
    }

    public static string ResolveSelection(IReadOnlyList<CatalogItem> items, string label)
    {
        // 手工输入原始名称仍可保存；标签只能完整、唯一命中，不能截掉任意名称的等级前缀。
        if (items.Any(i => i.Name == label)) return label;
        var matches = items.Where(i => Format(i) == label).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Name : label;
    }
}
