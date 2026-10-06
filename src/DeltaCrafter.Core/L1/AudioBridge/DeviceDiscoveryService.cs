using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace DeltaCrafter.Core.L1.AudioBridge;

public sealed record BridgeDevice(
    string DeviceId,
    string DeviceName,
    IPAddress Address,
    int AudioPort,
    int LevelPort,
    bool UsbMounted,
    bool UsbStreaming,
    DateTimeOffset LastStreamingSeen,
    DateTimeOffset LastSeen);

public sealed class DeviceDiscoveryService : IAsyncDisposable
{
    private static readonly TimeSpan DeviceTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan FallbackResolutionInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FallbackResolutionTimeout = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan SubnetProbeInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, BridgeDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly BridgeSettingsStore _settings;
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _udp;
    private Task? _sendTask;
    private Task? _receiveTask;
    private IReadOnlyList<IPEndPoint> _fallbackTargets = Array.Empty<IPEndPoint>();
    private DateTimeOffset _nextFallbackResolution = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSubnetProbe = DateTimeOffset.MinValue;

    public DeviceDiscoveryService(BridgeSettingsStore settings)
    {
        _settings = settings;
    }

    public event EventHandler? DevicesChanged;
    public Task Completion => Task.WhenAny(_sendTask ?? Task.CompletedTask, _receiveTask ?? Task.CompletedTask).Unwrap();

    public IReadOnlyList<BridgeDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.Values
                    .OrderBy(device => device.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public BridgeDevice? SelectedDevice
    {
        get
        {
            lock (_gate)
            {
                var selectedId = _settings.SelectedDeviceId;
                if (selectedId is not null && _devices.TryGetValue(selectedId, out var selected))
                {
                    return selected;
                }
                return _devices.Values.OrderBy(device => device.DeviceName).FirstOrDefault();
            }
        }
    }

    public void SelectDevice(string deviceId)
    {
        lock (_gate)
        {
            if (!_devices.ContainsKey(deviceId))
            {
                return;
            }
            _settings.SelectedDeviceId = deviceId;
        }
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task StartAsync()
    {
        if (_udp is not null)
        {
            return Task.CompletedTask;
        }

        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, AudioProtocol.DiscoveryPort));
        _udp.EnableBroadcast = true;

        _sendTask = Task.Run(() => SendLoopAsync(_stop.Token));
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_stop.Token));
        return Task.CompletedTask;
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var offer = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocol = "tdisplay-audio",
                version = AudioProtocol.Version,
                type = "offer",
                hostName = Environment.MachineName,
                httpPort = 8765,
            });

            foreach (var target in GetDiscoveryTargets())
            {
                try
                {
                    await _udp!.SendAsync(offer, offer.Length, target).ConfigureAwait(false);
                }
                catch (SocketException) when (!cancellationToken.IsCancellationRequested)
                {
                    // One unavailable adapter or broadcast route must not suppress other targets.
                }
            }

            if (DateTimeOffset.UtcNow >= _nextSubnetProbe)
            {
                foreach (var target in GetSubnetProbeTargets())
                {
                    try
                    {
                        await _udp!.SendAsync(offer, offer.Length, target).ConfigureAwait(false);
                    }
                    catch (SocketException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Continue probing the remaining addresses on this LAN.
                    }
                }
                _nextSubnetProbe = DateTimeOffset.UtcNow + SubnetProbeInterval;
            }

            try
            {
                if (DateTimeOffset.UtcNow >= _nextFallbackResolution)
                {
                    _fallbackTargets = await ResolveFallbackTargetsAsync(cancellationToken).ConfigureAwait(false);
                    _nextFallbackResolution = DateTimeOffset.UtcNow + FallbackResolutionInterval;
                }
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                // Network transitions are expected; the next resolution retries automatically.
            }

            ExpireDevices();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static IReadOnlyList<IPEndPoint> GetBroadcastTargets()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null)
                {
                    continue;
                }

                var ip = address.Address.GetAddressBytes();
                var mask = address.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (var index = 0; index < broadcast.Length; index++)
                {
                    broadcast[index] = (byte)(ip[index] | ~mask[index]);
                }
                addresses.Add(new IPAddress(broadcast));
            }
        }
        return addresses.Select(address => new IPEndPoint(address, AudioProtocol.DiscoveryPort)).ToArray();
    }

    private static IReadOnlyList<IPEndPoint> GetSubnetProbeTargets()
    {
        var targets = new HashSet<IPEndPoint>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || networkInterface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null)
                {
                    continue;
                }

                var localBytes = address.Address.GetAddressBytes();
                if (localBytes[0] == 127 || (localBytes[0] == 169 && localBytes[1] == 254))
                {
                    continue;
                }

                var maskBytes = address.IPv4Mask.GetAddressBytes();
                var local = ToUInt32(localBytes);
                var mask = ToUInt32(maskBytes);
                var network = local & mask;
                var broadcast = network | ~mask;
                var hostCount = broadcast > network ? broadcast - network - 1 : 0;

                if (hostCount is > 0 and <= 254)
                {
                    for (var candidate = network + 1; candidate < broadcast; candidate++)
                    {
                        if (candidate != local)
                        {
                            targets.Add(new IPEndPoint(FromUInt32(candidate), AudioProtocol.DiscoveryPort));
                        }
                    }
                }
                else
                {
                    // Large corporate/VPN masks are capped to the local /24 to avoid a broad scan.
                    for (var host = 1; host <= 254; host++)
                    {
                        if (host != localBytes[3])
                        {
                            targets.Add(new IPEndPoint(
                                new IPAddress(new[] { localBytes[0], localBytes[1], localBytes[2], (byte)host }),
                                AudioProtocol.DiscoveryPort));
                        }
                    }
                }
            }
        }
        return targets.ToArray();
    }

    private static uint ToUInt32(byte[] bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];

    private static IPAddress FromUInt32(uint value) => new(new[]
    {
        (byte)(value >> 24),
        (byte)(value >> 16),
        (byte)(value >> 8),
        (byte)value,
    });

    private IReadOnlyList<IPEndPoint> GetDiscoveryTargets()
    {
        var targets = new HashSet<IPEndPoint>(GetBroadcastTargets());
        lock (_gate)
        {
            foreach (var device in _devices.Values)
            {
                targets.Add(new IPEndPoint(device.Address, AudioProtocol.DiscoveryPort));
            }
        }
        foreach (var fallbackTarget in _fallbackTargets)
        {
            targets.Add(fallbackTarget);
        }
        return targets.ToArray();
    }

    private static async Task<IReadOnlyList<IPEndPoint>> ResolveFallbackTargetsAsync(
        CancellationToken cancellationToken)
    {
        var targets = new HashSet<IPEndPoint>();
        foreach (var hostName in new[] { "t-display-audio", "t-display-audio.local" })
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(FallbackResolutionTimeout);
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(hostName, timeout.Token).ConfigureAwait(false);
                foreach (var address in addresses.Where(address => address.AddressFamily == AddressFamily.InterNetwork))
                {
                    targets.Add(new IPEndPoint(address, AudioProtocol.DiscoveryPort));
                }
            }
            catch (SocketException)
            {
                // DHCP DNS and mDNS are optional fallbacks for networks that filter broadcasts.
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // A slow resolver must not delay the one-second discovery broadcast cadence.
            }
        }
        return targets.ToArray();
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _udp!.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            string? deviceId = null;
            string? deviceName = null;
            var audioPort = 0;
            var levelPort = AudioProtocol.LevelPort;
            var usbMounted = false;
            var usbStreaming = false;
            try
            {
                using var document = JsonDocument.Parse(received.Buffer);
                var root = document.RootElement;
                if (root.GetProperty("protocol").GetString() != "tdisplay-audio"
                    || root.GetProperty("type").GetString() != "ready"
                    || root.GetProperty("version").GetInt32() != AudioProtocol.Version)
                {
                    continue;
                }

                deviceId = root.GetProperty("deviceId").GetString();
                if (string.IsNullOrWhiteSpace(deviceId))
                {
                    continue;
                }
                deviceName = root.GetProperty("deviceName").GetString() ?? deviceId;
                audioPort = root.GetProperty("audioPort").GetInt32();
                levelPort = root.TryGetProperty("levelPort", out var levelPortProperty)
                    ? levelPortProperty.GetInt32()
                    : AudioProtocol.LevelPort;
                usbStreaming = root.GetProperty("usbStreaming").GetBoolean();
                usbMounted = root.TryGetProperty("usbMounted", out var mountedProperty)
                    ? mountedProperty.GetBoolean()
                    : usbStreaming;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                continue;
            }

            if (audioPort is < 1 or > 65535 || levelPort is < 1 or > 65535) continue;

            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var lastStreamingSeen = usbStreaming
                    ? now
                    : _devices.TryGetValue(deviceId, out var previous)
                        ? previous.LastStreamingSeen
                        : DateTimeOffset.MinValue;
                var device = new BridgeDevice(
                    deviceId,
                    deviceName!,
                    received.RemoteEndPoint.Address,
                    audioPort,
                    levelPort,
                    usbMounted,
                    usbStreaming,
                    lastStreamingSeen,
                    now);
                _devices[device.DeviceId] = device;
            }
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ExpireDevices()
    {
        var cutoff = DateTimeOffset.UtcNow - DeviceTimeout;
        var changed = false;
        lock (_gate)
        {
            foreach (var deviceId in _devices
                         .Where(pair => pair.Value.LastSeen < cutoff)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                changed |= _devices.Remove(deviceId);
            }
        }
        if (changed)
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp?.Dispose();

        var tasks = new[] { _sendTask, _receiveTask }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        _stop.Dispose();
    }
}
