using System.Net;
using System.Net.Sockets;

namespace DeltaCrafter.Core.L0;

public sealed record HarpPlayerSettings
{
    public string Url { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public static string NormalizeUrl(string? value) => (value ?? "").Trim().TrimEnd('/');
    // The desktop only connects to the locally configured LAN player, never a URL supplied by a remote action.
    public bool IsValid() => Uri.TryCreate(NormalizeUrl(Url), UriKind.Absolute, out var uri)
        && uri.Scheme == "http" && uri.UserInfo == "" && uri.AbsolutePath == "/"
        && uri.Query == "" && uri.Fragment == ""
        && IPAddress.TryParse(uri.Host, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork
        && IsLan(ip.GetAddressBytes()) && ApiKey is { Length: >= 32 and <= 64 } && ApiKey.All(char.IsAsciiLetterOrDigit);
    private static bool IsLan(byte[] b) => b[0] == 10 || (b[0] == 192 && b[1] == 168)
        || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 169 && b[1] == 254);
    public override string ToString() => "HarpPlayerSettings (credentials omitted)";
}

public sealed record HarpUpdateRequest(string Action, string RequestId, string CheckId = "", string PlayerUrl = "", string PlayerId = "")
{
    public bool IsValid() => Action is "check" or "install" && Guid.TryParseExact(RequestId, "N", out _)
        && (Action != "install" || Guid.TryParseExact(CheckId, "N", out _)) && PlayerUrl is { Length: <= 192 }
        && PlayerId is { Length: 0 or 12 } && PlayerId.All(char.IsAsciiHexDigit);
}

public sealed record HarpUpdateState
{
    public string RequestId { get; init; } = "";
    public string CheckId { get; init; } = "";
    public string PlayerUrl { get; init; } = "";
    public string PlayerId { get; init; } = "";
    public string Phase { get; init; } = "idle";
    public string Current { get; init; } = "";
    public string Version { get; init; } = "";
    public string Detail { get; init; } = "请先检查 Harp 更新";
    public bool Busy { get; init; }
    public bool Ready { get; init; }
    public int Percent { get; init; }
}
