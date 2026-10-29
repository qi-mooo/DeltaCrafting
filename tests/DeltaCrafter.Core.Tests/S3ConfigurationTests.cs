using System.Text;
using System.Text.Json;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class S3ConfigurationTests
{
    private static readonly S3DeviceInfo Modern = new("axeuh-tools-v32", "device-1", "10.0.0.194",
        "http://10.0.0.183:17890", "原来的Wi-Fi", true, true, false, true);
    private static readonly string Key = new('A', 64);

    [Fact]
    public void Address_change_does_not_send_or_replace_saved_wifi()
    {
        byte[] payload = S3ConfigurationClient.Configuration(Modern, "http://10.0.0.181:17890", Key, null, null);
        Assert.Equal((byte)'\n', payload[^1]);
        using var document = JsonDocument.Parse(payload.AsMemory(0, payload.Length - 1));
        var json = document.RootElement;
        Assert.Equal("api-configure", json.GetProperty("command").GetString());
        Assert.Equal("http://10.0.0.181:17890", json.GetProperty("baseUrl").GetString());
        Assert.False(json.TryGetProperty("wifiPassword", out _));
        Assert.False(json.TryGetProperty("wifiSsid", out _));
    }

    [Fact]
    public void Old_firmware_requires_wifi_and_uses_existing_full_configuration_protocol()
    {
        var old = Modern with { ApiConfigureSupported = false };
        Assert.Throws<ArgumentException>(() => S3ConfigurationClient.Configuration(old, Modern.BaseUrl, Key, null, null));
        string payload = Encoding.UTF8.GetString(S3ConfigurationClient.Configuration(old, Modern.BaseUrl, Key, "网络", "password"));
        using var json = JsonDocument.Parse(payload);
        Assert.Equal("configure", json.RootElement.GetProperty("command").GetString());
        Assert.Equal("网络", json.RootElement.GetProperty("wifiSsid").GetString());
    }

    [Theory]
    [InlineData("http://127.0.0.1:17890")]
    [InlineData("http://0.0.0.0:17890")]
    [InlineData("http://169.254.1.1:17890")]
    [InlineData("http://239.1.1.1:17890")]
    [InlineData("https://10.0.0.181:17890")]
    [InlineData("http://10.0.0.181:17890/api/v1/status")]
    [InlineData("http://user@10.0.0.181:17890")]
    [InlineData("http://10.0.0.181:17890?key=secret")]
    public void Invalid_endpoints_are_rejected(string url) =>
        Assert.Throws<ArgumentException>(() => S3ConfigurationClient.Configuration(Modern, url, Key, null, null));

    [Fact]
    public void Wifi_limits_use_utf8_bytes_and_partial_credentials_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => S3ConfigurationClient.Configuration(Modern, Modern.BaseUrl, Key, new string('网', 11), "pwd"));
        Assert.Throws<ArgumentException>(() => S3ConfigurationClient.Configuration(Modern, Modern.BaseUrl, Key, "wifi", null));
        Assert.Throws<ArgumentException>(() => S3ConfigurationClient.Configuration(Modern, Modern.BaseUrl, Key, "wifi\0other", "pwd"));
        Assert.Throws<ArgumentException>(() => S3ConfigurationClient.Configuration(Modern with { WifiSsid = "" }, Modern.BaseUrl, Key, null, null));
        Assert.NotEmpty(S3ConfigurationClient.Configuration(Modern, Modern.BaseUrl, Key, "open-wifi", ""));
    }

    [Fact]
    public void Discovery_accepts_existing_display_firmware_but_not_harp_or_bootloader()
    {
        const string old = "{\"ok\":true,\"firmware\":\"axeuh-tools-v31\",\"configured\":true,\"wifiConnected\":true,\"apiOnline\":false,\"ip\":\"10.0.0.194\"}";
        var info = S3ConfigurationClient.ParseInfo(old);
        Assert.NotNull(info); Assert.False(info.ApiConfigureSupported); Assert.Equal("10.0.0.194", info.Ip);
        Assert.Null(S3ConfigurationClient.ParseInfo(old.Replace("axeuh-tools-v31", "delta-harp-v11")));
        Assert.Null(S3ConfigurationClient.ParseInfo("ESP-ROM:esp32s3-20210327"));
        Assert.Null(S3ConfigurationClient.ParseInfo("[]"));
        Assert.Null(S3ConfigurationClient.ParseInfo("{\"ok\":true}"));
        Assert.Null(S3ConfigurationClient.ParseInfo(new string('x', 4097)));
    }

    [Fact]
    public void Same_subnet_wins_over_another_default_gateway_and_ambiguity_is_not_guessed()
    {
        var lan = new S3ApiAddress("10.0.0.181", 24, "Ethernet", 17890, true);
        var vpn = new S3ApiAddress("192.168.50.2", 24, "VPN", 17890, true);
        var old = lan with { Address = "10.0.0.183" };
        Assert.Equal(lan, S3ConfigurationClient.ChooseAddress([vpn, lan], "10.0.0.194"));
        Assert.Null(S3ConfigurationClient.ChooseAddress([lan, old], "10.0.0.194"));
        Assert.Null(S3ConfigurationClient.ChooseAddress([vpn, lan], "0.0.0.0"));
        Assert.Equal(lan, S3ConfigurationClient.ChooseAddress([vpn with { HasGateway = false }, lan], "0.0.0.0"));
        Assert.Null(S3ConfigurationClient.ChooseAddress([], "10.0.0.194"));
    }
}
