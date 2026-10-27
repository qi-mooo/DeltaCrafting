using System.Net;
using System.Text.Json;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class HarpDiscoveryTests
{
    private const string Nonce="0123456789abcdef0123456789abcdef";
    private static Dictionary<string,object> Reply() => new() {
        ["type"]="delta-harp-device",["protocol"]=1,["nonce"]=Nonce,["deviceId"]="68ee8f6d4a44",
        ["name"]="DeltaHarp-6d4a44",["board"]="esp32-s3-dongle-fn8",["firmware"]="delta-harp-v3",
        ["port"]=80,["pairable"]=true,["challenge"]=Nonce
    };
    [Fact]
    public void Discovery_uses_sender_address_and_stable_identity_not_advertised_url()
    {
        var reply=Reply(); reply["url"]="http://8.8.8.8";
        var found=HarpDiscoveryClient.Parse(JsonSerializer.SerializeToUtf8Bytes(reply),IPAddress.Parse("10.0.0.194"),Nonce);
        Assert.NotNull(found); Assert.Equal("http://10.0.0.194",found.Url); Assert.Equal("68ee8f6d4a44",found.DeviceId);
        Assert.True(found.Pairable); Assert.DoesNotContain("apiKey",JsonSerializer.Serialize(reply));
        reply["pairable"]=false; reply.Remove("challenge");
        found=HarpDiscoveryClient.Parse(JsonSerializer.SerializeToUtf8Bytes(reply),IPAddress.Parse("192.168.1.2"),Nonce);
        Assert.NotNull(found); Assert.False(found.Pairable); Assert.Equal("",found.Challenge);
    }
    [Theory]
    [InlineData("nonce","different")]
    [InlineData("deviceId","bad")]
    [InlineData("type","another-device")]
    [InlineData("board","lilygo-t-display-s3")]
    [InlineData("challenge","short")]
    [InlineData("name","bad\nname")]
    [InlineData("port",81)]
    [InlineData("protocol",2)]
    public void Mismatched_or_malformed_announcements_are_ignored(string field,object value)
    {
        var reply=Reply(); reply[field]=value;
        Assert.Null(HarpDiscoveryClient.Parse(JsonSerializer.SerializeToUtf8Bytes(reply),IPAddress.Parse("10.0.0.194"),Nonce));
    }
    [Fact]
    public void Oversized_invalid_json_and_non_LAN_senders_are_ignored()
    {
        Assert.Null(HarpDiscoveryClient.Parse(new byte[1025],IPAddress.Parse("10.0.0.194"),Nonce));
        Assert.Null(HarpDiscoveryClient.Parse("{"u8.ToArray(),IPAddress.Parse("10.0.0.194"),Nonce));
        Assert.Null(HarpDiscoveryClient.Parse(JsonSerializer.SerializeToUtf8Bytes(Reply()),IPAddress.Parse("8.8.8.8"),Nonce));
        Assert.Null(HarpDiscoveryClient.Parse("{}"u8.ToArray(),IPAddress.Parse("10.0.0.194"),Nonce));
    }
}
