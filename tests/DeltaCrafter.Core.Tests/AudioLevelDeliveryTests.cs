using System.Net;
using System.Net.Sockets;
using DeltaCrafter.Core.L1.AudioBridge;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class AudioLevelDeliveryTests
{
    [Fact]
    public async Task Old_and_new_displays_receive_identical_levels_regardless_of_usb_selection()
    {
        using var oldDisplay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var newDisplay = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string root = Path.Combine(Path.GetTempPath(), "delta-level-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new BridgeSettingsStore(root) { SelectedDeviceId = "old-s3" };
            await using var discovery = new DeviceDiscoveryService(settings);
            await using var streaming = new AudioStreamingService(discovery, new WindowsAudioService());
            await streaming.SendLevelsAsync([
                Device("old-s3", oldDisplay, usbMounted: true),
                Device("new-s3", newDisplay, usbMounted: false),
            ], 64, timeout.Token);

            var oldPacket = await oldDisplay.ReceiveAsync(timeout.Token);
            var newPacket = await newDisplay.ReceiveAsync(timeout.Token);
            Assert.Equal(AudioProtocol.CreateLevelPacket(64), oldPacket.Buffer);
            Assert.Equal(oldPacket.Buffer, newPacket.Buffer);
            Assert.Equal(2, streaming.SentLevelPackets);
            Assert.Equal(2, streaming.LevelDelivery.Targets.Count);
            Assert.Equal("old-s3", settings.SelectedDeviceId);

            // The next discovery snapshot drops the old display; it must stop receiving.
            await streaming.SendLevelsAsync([Device("new-s3", newDisplay, usbMounted: true)], 23, timeout.Token);
            Assert.Equal(AudioProtocol.CreateLevelPacket(23), (await newDisplay.ReceiveAsync(timeout.Token)).Buffer);
            Assert.False(oldDisplay.Client.Poll(100_000, SelectMode.SelectRead));
            Assert.Equal(newDisplay.Client.LocalEndPoint!.ToString(), Assert.Single(streaming.LevelDelivery.Targets));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Pausing_suppresses_all_levels_and_resuming_uses_current_devices()
    {
        using var display = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string root = Path.Combine(Path.GetTempPath(), "delta-level-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var discovery = new DeviceDiscoveryService(new BridgeSettingsStore(root));
            await using var streaming = new AudioStreamingService(discovery, new WindowsAudioService()) { Paused = true };
            BridgeDevice[] devices = [Device("s3", display, usbMounted: false)];
            await streaming.SendLevelsAsync(devices, 80, timeout.Token);
            Assert.False(display.Client.Poll(100_000, SelectMode.SelectRead));
            Assert.Equal(0, streaming.SentLevelPackets);
            Assert.Empty(streaming.LevelDelivery.Targets);

            streaming.Paused = false;
            await streaming.SendLevelsAsync(devices, 0, timeout.Token);
            Assert.Equal(AudioProtocol.CreateLevelPacket(0), (await display.ReceiveAsync(timeout.Token)).Buffer);
            await streaming.SendLevelsAsync([], 50, timeout.Token);
            Assert.Equal(1, streaming.SentLevelPackets);
            Assert.Empty(streaming.LevelDelivery.Targets);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BridgeDevice Device(string id, UdpClient receiver, bool usbMounted) => new(
        id, "T-Display S3", IPAddress.Loopback, AudioProtocol.AudioPort,
        ((IPEndPoint)receiver.Client.LocalEndPoint!).Port, usbMounted, false,
        DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
}
