namespace DeltaCrafter.Core.L0;

public static class BlkAmmoIdentity
{
    public static string Name(int grade) => grade switch
    {
        3 => ".300BLK三级弹", 4 => ".300BLK四级弹", 5 => ".300BLK五级弹",
        _ => throw new ArgumentOutOfRangeException(nameof(grade)),
    };

    public static string Canonical(string name) => CatalogNameResolver.Canonical(name);

    public static bool IsBareName(string name) => Canonical(name) == "300BLK";

    public static int? Grade(string name) => Canonical(name) switch
    {
        "300BLK三级弹" or "300BLK3级弹" or "300BLK_3" or "300BLKSUB-3" => 3,
        "300BLK四级弹" or "300BLK4级弹" or "300BLK_4" or "300BLKSUB-4" => 4,
        "300BLK五级弹" or "300BLK5级弹" or "300BLK_5" => 5,
        _ => null,
    };
}
