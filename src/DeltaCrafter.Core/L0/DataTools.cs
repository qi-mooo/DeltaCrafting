namespace DeltaCrafter.Core.L0;

public sealed record DataToolQuery(string Tool, int Page = 1, string Mode = "gun", string Search = "")
{
    public bool IsValid() => Tool is "password" or "market" or "gun"
        && Page is >= 1 and <= 1000 && Mode is "gun" or "operator"
        && Search.Length <= 60 && !Search.Any(char.IsControl)
        && (Tool == "gun" || Page == 1 && Search.Length == 0 && Mode == "gun");
    public string Title => Tool switch { "password" => "今日密码", "market" => "当前集市物品", _ => "改枪码" };
}

public sealed record DataToolEntry(string Id, string Title, IReadOnlyList<string> Lines, string CopyText = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Detail => string.Join("\n", Lines);
}
public sealed record DataToolResult(string Tool, string Title, int Page, bool HasNext,
    string Detail, IReadOnlyList<DataToolEntry> Entries, DateTimeOffset FetchedAt);

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record DataToolCopyRequest(string Code)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Code) && Code.Length <= 256 && !Code.Any(char.IsControl);
}
