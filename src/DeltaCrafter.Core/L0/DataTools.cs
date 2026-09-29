namespace DeltaCrafter.Core.L0;

public sealed record DataToolQuery(string Tool, int Page = 1, string Mode = "gun", string Search = "",
    string Category = "全部", string Weapon = "全部")
{
    public bool IsValid() => Tool is "password" or "market" or "gun" or "gun-keys"
        && Page is >= 1 and <= 1000 && Mode is "gun" or "operator"
        && Search.Length <= 60 && !Search.Any(char.IsControl)
        && ValidFilter(Category) && ValidFilter(Weapon)
        && (Tool == "gun" || Page == 1 && Search.Length == 0 && Category == "全部" && Weapon == "全部"
            && (Tool == "gun-keys" || Mode == "gun"));
    private static bool ValidFilter(string value) => value.Length is > 0 and <= 60 && !value.Any(char.IsControl);
    public string Title => Tool switch { "password" => "今日密码", "market" => "当前集市物品", "gun-keys" => "选择枪械", _ => "改枪码" };
}

public sealed record DataToolEntry(string Id, string Title, IReadOnlyList<string> Lines, string CopyText = "",
    string Password = "", string Date = "", string ImageUrl = "", string Price = "", string Author = "", string Category = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ListTitle => Password.Length > 0 ? $"{Title}  {Password}" : Author.Length > 0 ? $"{Title} · {Author}" : Title;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Detail => string.Join("\n", Lines);
}
public sealed record DataToolResult(string Tool, string Title, int Page, bool HasNext,
    string Detail, IReadOnlyList<DataToolEntry> Entries, DateTimeOffset FetchedAt, DateTimeOffset? ExpiresAt = null, int TotalPages = 1);

public sealed record DataToolImage(int Width, int Height, string Pixels);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record DataToolCopyRequest(string Code)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Code) && Code.Length <= 256 && !Code.Any(char.IsControl);
}
