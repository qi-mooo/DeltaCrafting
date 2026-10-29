using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DeltaCrafter.Core.L1;

public sealed record S3DeviceInfo(string Firmware, string DeviceId, string Ip, string BaseUrl,
    string WifiSsid, bool Configured, bool WifiConnected, bool ApiOnline, bool ApiConfigureSupported);

public sealed record S3UsbDevice(EspPortInfo Port, S3DeviceInfo Info)
{
    public string Label => $"{Port.PortName} · {Info.Firmware} · {(Info.WifiConnected ? Info.Ip : "Wi-Fi 未连接")}";
}

public sealed record S3ApiAddress(string Address, int PrefixLength, string Adapter, int Port, bool HasGateway)
{
    public string Url => $"http://{Address}:{Port}";
    public string Label => $"{Url} · {Adapter}";
}

/// <summary>运行中 S3 的 USB 配置协议。先核对固件身份,不复位、不向未知串口发送凭据。</summary>
public static class S3ConfigurationClient
{
    public static IReadOnlyList<S3ApiAddress> LocalAddresses(int port)
    {
        var result = new List<S3ApiAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            var properties = adapter.GetIPProperties();
            bool gateway = properties.GatewayAddresses.Any(a => UsableAddress(a.Address));
            foreach (var unicast in properties.UnicastAddresses)
                if (UsableAddress(unicast.Address))
                    result.Add(new(unicast.Address.ToString(), unicast.PrefixLength, adapter.Name, port, gateway));
        }
        return result.DistinctBy(a => a.Address)
            .OrderByDescending(a => a.HasGateway).ThenBy(a => a.Adapter).ToArray();
    }

    public static S3ApiAddress? ChooseAddress(IReadOnlyList<S3ApiAddress> addresses, string? deviceIp)
    {
        if (IPAddress.TryParse(deviceIp, out var target) && UsableAddress(target))
        {
            var matching = addresses.Where(a => SameSubnet(a, target)).ToArray();
            if (matching.Length == 1) return matching[0];
            if (matching.Length > 1) return null;
        }
        var gateways = addresses.Where(a => a.HasGateway).ToArray();
        return gateways.Length == 1 ? gateways[0] : addresses.Count == 1 ? addresses[0] : null;
    }

    private static bool SameSubnet(S3ApiAddress address, IPAddress target)
    {
        if (!IPAddress.TryParse(address.Address, out var local) || !UsableAddress(local)
            || address.PrefixLength is < 1 or > 32) return false;
        byte[] left = local.GetAddressBytes(), right = target.GetAddressBytes();
        for (int i = 0; i < 4; i++)
        {
            int bits = Math.Clamp(address.PrefixLength - i * 8, 0, 8);
            int mask = (0xff << (8 - bits)) & 0xff;
            if ((left[i] & mask) != (right[i] & mask)) return false;
        }
        return true;
    }

    private static bool UsableAddress(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] is > 0 and < 224 && bytes[0] != 127 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    public static S3DeviceInfo? ParseInfo(string line)
    {
        if (line.Length > 4096) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Flag(root, "ok")) return null;
            string firmware = Text(root, "firmware");
            if (!firmware.StartsWith("axeuh-tools-v", StringComparison.Ordinal)
                || !root.TryGetProperty("configured", out _) || !root.TryGetProperty("apiOnline", out _)) return null;
            string board = Text(root, "board");
            if (board.Length > 0 && board != "lilygo-t-display-s3") return null;
            return new(firmware, Text(root, "deviceId"), Text(root, "ip"), Text(root, "baseUrl"),
                Text(root, "wifiSsid"), Flag(root, "configured"), Flag(root, "wifiConnected"),
                Flag(root, "apiOnline"), Flag(root, "apiConfigureSupported"));
        }
        catch (JsonException) { return null; }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: <= 192 } text && !text.Any(char.IsControl) ? text : "";
    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public static byte[] Configuration(S3DeviceInfo device, string url, string key, string? ssid, string? password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http"
            || !IPAddress.TryParse(uri.Host, out var ip) || !UsableAddress(ip)
            || uri.Port is < 1024 or > 65535 || uri.AbsolutePath != "/"
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw new ArgumentException("请选择本机有效的局域网接口地址。");
        if (key.Length is < 32 or > 128 || !key.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException("电脑的配对密钥无效,请在设备设置中重新生成。");
        bool wifi = ssid is not null || password is not null;
        if (wifi && (string.IsNullOrEmpty(ssid) || password is null
            || Encoding.UTF8.GetByteCount(ssid) > 32 || Encoding.UTF8.GetByteCount(password) > 64
            || ssid.Contains('\0') || password.Contains('\0')))
            throw new ArgumentException("请填写有效的 2.4 GHz Wi-Fi 名称和密码;开放网络的密码可留空。");
        if (!wifi && (!device.ApiConfigureSupported || string.IsNullOrEmpty(device.WifiSsid)))
            throw new ArgumentException(device.ApiConfigureSupported
                ? "设备尚未保存 Wi-Fi,请填写 Wi-Fi 名称和密码。"
                : "旧固件需重新填写 Wi-Fi;也可先通过 USB 升级 S3 固件,再保留原 Wi-Fi 更新地址。");
        var payload = new Dictionary<string, object>
        {
            ["command"] = device.ApiConfigureSupported ? "api-configure" : "configure",
            ["baseUrl"] = url.TrimEnd('/'), ["apiKey"] = key,
        };
        if (wifi) { payload["wifiSsid"] = ssid!; payload["wifiPassword"] = password!; }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length >= 2048) throw new ArgumentException("配置内容过长。");
        return [.. bytes, (byte)'\n'];
    }

    public static async Task<IReadOnlyList<S3UsbDevice>> ScanAsync(IReadOnlyList<EspPortInfo> ports, CancellationToken ct)
    {
        await SerialEspLink.OperationLock.WaitAsync(ct);
        try
        {
            return await Task.Run<IReadOnlyList<S3UsbDevice>>(() =>
            {
                var found = new List<S3UsbDevice>();
                foreach (var port in ports.Where(p => p.IsEspNative))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        using var link = new SerialEspLink(port.PortName);
                        if (ReadInfo(link, ct) is { } info) found.Add(new(port, info));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException or ArgumentException) { }
                }
                return found;
            }, ct);
        }
        finally { SerialEspLink.OperationLock.Release(); }
    }

    public static async Task<S3DeviceInfo?> ConfigureAsync(S3UsbDevice device, string url, string key,
        string? ssid, string? password, CancellationToken ct)
    {
        await SerialEspLink.OperationLock.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                using var link = new SerialEspLink(device.Port.PortName);
                var current = ReadInfo(link, ct) ?? throw new IOException("未识别到运行中的 S3 屏幕,请检查连接并退出声音控制。");
                if (device.Info.DeviceId.Length > 0 && current.DeviceId != device.Info.DeviceId)
                    throw new IOException("串口对应的设备已改变,请重新识别后选择。");
                byte[] configuration = Configuration(current, url, key, ssid, password);
                try { link.Write(configuration); }
                finally { Array.Clear(configuration); }
                var watch = Stopwatch.StartNew();
                var pending = new List<byte>();
                while (watch.Elapsed < TimeSpan.FromSeconds(5))
                {
                    string? line = ReadLine(link, pending, TimeSpan.FromSeconds(1), ct);
                    if (line is null) continue;
                    if (ParseInfo(line) is not null) continue;
                    try
                    {
                        using var reply = JsonDocument.Parse(line);
                        var root = reply.RootElement;
                        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out _)) continue;
                        if (Flag(root, "ok") && Flag(root, "restart")) return;
                        throw new IOException("设备未接受配置,请检查固件版本和 Wi-Fi 信息后重试。");
                    }
                    catch (JsonException) { }
                }
                throw new IOException("未收到保存确认;设备可能已重启,请重新识别后检查连接状态。");
            }, ct);
            S3DeviceInfo? latest = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await Task.Delay(1500, ct);
                try
                {
                    latest = await Task.Run(() =>
                    {
                        using var link = new SerialEspLink(device.Port.PortName);
                        return ReadInfo(link, ct);
                    }, ct);
                    if (latest is not null && ((device.Info.DeviceId.Length > 0 && latest.DeviceId != device.Info.DeviceId)
                        || (latest.BaseUrl.Length > 0 && latest.BaseUrl.TrimEnd('/') != url.TrimEnd('/'))))
                        latest = null;
                    if (latest?.ApiOnline == true) return latest;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException) { }
            }
            return latest;
        }
        finally { SerialEspLink.OperationLock.Release(); }
    }

    private static S3DeviceInfo? ReadInfo(IEspSerialLink link, CancellationToken ct)
    {
        link.DiscardInput();
        var pending = new List<byte>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            link.Write("{\"command\":\"info\"}\n"u8.ToArray());
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMilliseconds(1500))
                if (ReadLine(link, pending, TimeSpan.FromMilliseconds(300), ct) is { } line && ParseInfo(line) is { } info) return info;
        }
        return null;
    }

    private static string? ReadLine(IEspSerialLink link, List<byte> bytes, TimeSpan timeout, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();
            int next = link.ReadByte(TimeSpan.FromMilliseconds(100));
            if (next < 0) continue;
            if (next == '\n')
            {
                string line = Encoding.UTF8.GetString(bytes.ToArray()).Trim();
                bytes.Clear();
                return line;
            }
            if (bytes.Count >= 4096) { bytes.Clear(); return null; }
            bytes.Add((byte)next);
        }
        return null;
    }
}
