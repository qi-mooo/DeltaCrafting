using System.IO.Ports;
using Microsoft.Win32;

namespace DeltaCrafter.Core.L1;

/// <summary>基于 System.IO.Ports 的 ESP 下载串口。原生 USB(USB-Serial/JTAG)与转串口芯片通用。</summary>
public sealed class SerialEspLink : IEspSerialLink
{
    private readonly SerialPort _port;

    public SerialEspLink(string portName)
    {
        _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            ReadBufferSize = 64 * 1024,
            WriteBufferSize = 64 * 1024,
            WriteTimeout = 5000,
            // 打开时不主动拉 DTR/RTS,避免在复位序列之外意外复位芯片。
            DtrEnable = false,
            RtsEnable = false,
        };
        _port.Open();
    }

    /// <summary>
    /// 当前可用串口。ESP32-S3 原生 USB(USB-Serial/JTAG,VID 303A PID 1001)排在最前:
    /// 全新芯片与 T-Display 固件都以它枚举,通常无需按 BOOT。设备描述取自注册表 USB 枚举项。
    /// </summary>
    public static IReadOnlyList<EspPortInfo> Ports()
    {
        var known = new Dictionary<string, EspPortInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            foreach (string device in usb?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                bool native = device.StartsWith("VID_303A&PID_1001", StringComparison.OrdinalIgnoreCase);
                using var deviceKey = usb!.OpenSubKey(device);
                foreach (string instance in deviceKey?.GetSubKeyNames() ?? Array.Empty<string>())
                {
                    using var instanceKey = deviceKey!.OpenSubKey(instance);
                    using var parameters = instanceKey?.OpenSubKey("Device Parameters");
                    if (parameters?.GetValue("PortName") is not string port) continue;
                    string description = instanceKey!.GetValue("FriendlyName") as string ?? device;
                    known[port] = new(port, description, native);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

        return SerialPort.GetPortNames().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(port => known.TryGetValue(port, out var info) ? info : new EspPortInfo(port, port, false))
            .OrderByDescending(p => p.IsEspNative)
            .ThenBy(p => p.PortName.Length).ThenBy(p => p.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public int BaudRate
    {
        get => _port.BaudRate;
        set => _port.BaudRate = value;
    }

    public void Write(byte[] data) => _port.Write(data, 0, data.Length);

    public int ReadByte(TimeSpan timeout)
    {
        _port.ReadTimeout = Math.Max(1, (int)Math.Min(int.MaxValue, timeout.TotalMilliseconds));
        try { return _port.ReadByte(); }
        catch (TimeoutException) { return -1; }
    }

    public void DiscardInput() => _port.DiscardInBuffer();

    public void SetDtr(bool value) => _port.DtrEnable = value;

    public void SetRts(bool value) => _port.RtsEnable = value;

    public void Dispose() => _port.Dispose();
}

public sealed record EspPortInfo(string PortName, string Description, bool IsEspNative)
{
    /// <summary>友好名通常已含 COM 号,如「USB 串行设备 (COM5)」;不含时补上。</summary>
    public string Label => (Description.Contains(PortName, StringComparison.OrdinalIgnoreCase)
        ? Description : $"{Description} ({PortName})") + (IsEspNative ? " · ESP32-S3 USB" : "");
}
