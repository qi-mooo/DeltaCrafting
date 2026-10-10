using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

public sealed record HarpDiscoveredDevice(string DeviceId, string Name, string Url, string Firmware, bool Pairable, string Challenge)
{
    public string Label => $"{Name} · {Url} · {Firmware}";
    public override string ToString() => Label;
}

public static class HarpDiscoveryClient
{
    public const int Port = 40110;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static HarpDiscoveredDevice? Parse(byte[] bytes, IPAddress sender, string nonce)
    {
        if (bytes.Length > 1024) return null;
        try
        {
            using var d = JsonDocument.Parse(bytes); var r = d.RootElement;
            if (r.GetProperty("type").GetString() != "delta-harp-device" || r.GetProperty("protocol").GetInt32() != 1
                || r.GetProperty("nonce").GetString() != nonce || r.GetProperty("board").GetString() != "esp32-s3-dongle-fn8"
                || r.GetProperty("port").GetInt32() != 80) return null;
            string id = r.GetProperty("deviceId").GetString() ?? "", name = r.GetProperty("name").GetString() ?? "";
            string version = r.GetProperty("firmware").GetString() ?? "";
            bool pairable = r.GetProperty("pairable").GetBoolean();
            string challenge = pairable ? r.GetProperty("challenge").GetString() ?? "" : "";
            string url = "http://" + sender;
            if (!Hex(id,12) || name.Length is < 1 or > 48 || name.Any(char.IsControl) || version.Length is < 1 or > 48
                || (pairable && !Hex(challenge,32)) || !(new HarpPlayerSettings { Url=url, ApiKey=new string('a',32) }).IsValid()) return null;
            return new(id,name,url,version,pairable,challenge);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return null; }
    }
    private static bool Hex(string s,int length) => s.Length==length && s.All(char.IsAsciiHexDigit);
    public static async Task<IReadOnlyList<HarpDiscoveredDevice>> ScanAsync(CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any,0)) { EnableBroadcast=true };
        string nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        byte[] query=JsonSerializer.SerializeToUtf8Bytes(new {type="delta-harp-discover",protocol=1,nonce});
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(a=>a.OperationalStatus==OperationalStatus.Up))
        foreach(var address in adapter.GetIPProperties().UnicastAddresses.Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork))
        {
            byte[] ip=address.Address.GetAddressBytes(),mask=address.IPv4Mask.GetAddressBytes();
            if(IPAddress.IsLoopback(address.Address)) continue;
            targets.Add(new IPAddress(ip.Select((b,i)=>(byte)(b | ~mask[i])).ToArray()));
        }
        var found = new Dictionary<string,HarpDiscoveredDevice>();
        for(int round=0;round<3;++round)
        {
            foreach(var address in targets)
            {
                try { await udp.SendAsync(query,new IPEndPoint(address,Port),ct); }
                catch(SocketException) { }
            }
            using var window=CancellationTokenSource.CreateLinkedTokenSource(ct); window.CancelAfter(900);
            try
            {
                while(true)
                {
                    var reply=await udp.ReceiveAsync(window.Token);
                    if(reply.RemoteEndPoint.Port==Port && Parse(reply.Buffer,reply.RemoteEndPoint.Address,nonce) is {} device)
                        if(found.Count<32 || found.ContainsKey(device.DeviceId)) found[device.DeviceId]=device;
                }
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested) { }
        }
        return found.Values.OrderBy(d=>d.Name,StringComparer.Ordinal).ToArray();
    }
    public static async Task<HarpPlayerSettings> PairAsync(HarpDiscoveredDevice device,CancellationToken ct)
    {
        if(!device.Pairable || !Hex(device.Challenge,32)) throw new InvalidOperationException("请按住播放器 BOOT 一秒,然后重新扫描");
        using var client=new HttpClient(new SocketsHttpHandler {UseProxy=false,AllowAutoRedirect=false}) {Timeout=TimeSpan.FromSeconds(5),MaxResponseContentBufferSize=2048};
        using var content=new StringContent(JsonSerializer.Serialize(new {deviceId=device.DeviceId,challenge=device.Challenge}),Encoding.UTF8,"application/json");
        using var response=await client.PostAsync(device.Url+"/api/v1/pair",content,ct);
        if(!response.IsSuccessStatusCode) throw new InvalidOperationException("配对窗口已关闭,请按 BOOT 后重新扫描");
        await response.Content.LoadIntoBufferAsync(2048);
        using var d=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        var r=d.RootElement;
        if(!r.GetProperty("ok").GetBoolean() || r.GetProperty("deviceId").GetString()!=device.DeviceId) throw new InvalidOperationException("播放器身份不匹配");
        var settings=new HarpPlayerSettings {Url=device.Url,DeviceId=device.DeviceId,ApiKey=r.GetProperty("apiKey").GetString() ?? ""};
        if(!settings.IsValid()) throw new InvalidOperationException("播放器配对响应无效");
        return settings;
    }
}
