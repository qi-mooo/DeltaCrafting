using System.IO.Compression;
using System.Security.Cryptography;

namespace DeltaCrafter.Core.L1;

/// <summary>串口抽象:真实实现见 SerialEspLink;单元测试用模拟 ROM 替换。</summary>
public interface IEspSerialLink : IDisposable
{
    int BaudRate { get; set; }
    void Write(byte[] data);
    /// <summary>读一个字节;超时返回 -1。</summary>
    int ReadByte(TimeSpan timeout);
    void DiscardInput();
    void SetDtr(bool value);
    void SetRts(bool value);
}

public sealed record EspFlashProgress(string Stage, double Fraction);

/// <summary>
/// ESP32-S3 ROM 串口下载协议(不加载 stub,只用 ROM 自带命令),等价于
/// esptool --before default_reset write_flash --compress 后再逐映像 MD5 校验。
/// 用于从未装过本项目固件、无法在线更新的板子。
/// </summary>
public sealed class EspRomFlasher
{
    private const byte CmdSync = 0x08, CmdReadReg = 0x0A, CmdSpiSetParams = 0x0B, CmdSpiAttach = 0x0D,
        CmdChangeBaud = 0x0F, CmdDeflBegin = 0x10, CmdDeflData = 0x11, CmdFlashMd5 = 0x13;

    /// <summary>ROM 单块写入上限(stub 可到 16 KB,ROM 只接受 1 KB)。</summary>
    internal const int RomWriteBlock = 0x400;
    /// <summary>ESP32-S3 芯片识别寄存器的 ROM 魔数(esptool CHIP_DETECT_MAGIC_VALUE)。</summary>
    internal const uint ChipDetectRegister = 0x40001000, Esp32S3Magic = 0x9;
    private const int RomBaud = 115200;

    private readonly IEspSerialLink _link;
    private readonly int _targetBaud;
    private readonly Action<TimeSpan> _sleep;
    private readonly EspSlip.Decoder _decoder = new();

    public EspRomFlasher(IEspSerialLink link, int targetBaud = 460800, Action<TimeSpan>? sleep = null)
    {
        _link = link;
        _targetBaud = targetBaud;
        _sleep = sleep ?? Thread.Sleep;
    }

    /// <summary>进入下载模式、写入全部映像并逐一 MD5 校验,最后复位运行新固件。</summary>
    public void Flash(EspFlashBundle bundle, IProgress<EspFlashProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new("正在连接芯片", 0));
        Connect(ct);
        uint magic = ReadReg(ChipDetectRegister);
        if (magic != Esp32S3Magic)
            throw new InvalidOperationException($"连接到的芯片不是 ESP32-S3(识别值 0x{magic:X})。请确认选择了正确的串口。");

        if (_targetBaud != RomBaud)
        {
            // ROM 的第二个参数(旧波特率)必须为 0;原生 USB 串口忽略波特率,此命令无副作用。
            Command(CmdChangeBaud, Words((uint)_targetBaud, 0));
            _link.BaudRate = _targetBaud;
            _sleep(TimeSpan.FromMilliseconds(50));
            _link.DiscardInput();
        }

        // ESP32-S3 ROM 的 SPI_ATTACH 需要 8 字节参数;全 0 = 默认 SPI 引脚。
        Command(CmdSpiAttach, new byte[8]);
        Command(CmdSpiSetParams, Words(0, (uint)bundle.FlashSizeBytes, 64 * 1024, 4 * 1024, 256, 0xFFFF));

        long total = bundle.Images.Sum(i => (long)i.Data.Length), done = 0;
        foreach (var image in bundle.Images)
        {
            ct.ThrowIfCancellationRequested();
            byte[] payload = bundle.PayloadFor(image);
            WriteCompressed(image, payload, fraction =>
                progress?.Report(new($"正在写入 {image.File}", (done + fraction * image.Data.Length) / total)), ct);
            done += image.Data.Length;

            progress?.Report(new($"正在校验 {image.File}", (double)done / total));
            string expected = Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();
            string actual = FlashMd5(image.Offset, payload.Length);
            if (actual != expected)
                throw new InvalidOperationException($"{image.File} 写入后校验不一致(期望 {expected},读回 {actual})。请重新刷写。");
        }

        // 不发 FLASH_DEFL_END:ROM 收到后会直接退出下载模式(esptool 对 ROM 同样跳过),
        // 由复位信号启动新固件。
        progress?.Report(new("正在重启设备", 1));
        HardReset();
    }

    private void WriteCompressed(EspFlashImage image, byte[] payload, Action<double> report, CancellationToken ct)
    {
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(payload);
            compressed = buffer.ToArray();
        }
        int blocks = (compressed.Length + RomWriteBlock - 1) / RomWriteBlock;
        // ROM 按「擦除块数 × 块大小」擦除(esptool flash_defl_begin 的 ROM 分支)。
        int eraseBlocks = (payload.Length + RomWriteBlock - 1) / RomWriteBlock;
        uint writeSize = (uint)(eraseBlocks * RomWriteBlock);
        // ROM 在 BEGIN 时整段擦除:超时按每 MB 30 秒(esptool ERASE_REGION_TIMEOUT_PER_MB),至少 10 秒。
        var eraseTimeout = TimeSpan.FromSeconds(Math.Max(10, 30.0 * writeSize / (1 << 20)));
        // 第 5 个字 = 不加密写入;ESP32-S3 ROM 的 BEGIN 必须带此字段。
        Command(CmdDeflBegin, Words(writeSize, (uint)blocks, RomWriteBlock, (uint)image.Offset, 0), eraseTimeout);

        for (int seq = 0; seq < blocks; seq++)
        {
            ct.ThrowIfCancellationRequested();
            int start = seq * RomWriteBlock;
            var block = compressed.AsSpan(start, Math.Min(RomWriteBlock, compressed.Length - start));
            var data = new byte[16 + block.Length];
            BitConverter.TryWriteBytes(data.AsSpan(0), (uint)block.Length);
            BitConverter.TryWriteBytes(data.AsSpan(4), (uint)seq);
            block.CopyTo(data.AsSpan(16));
            Command(CmdDeflData, data, TimeSpan.FromSeconds(10), Checksum(block));
            report((double)(seq + 1) / blocks);
        }
    }

    private string FlashMd5(int offset, int length)
    {
        // ROM 版返回 32 个十六进制 ASCII 字符(stub 版才返回 16 字节原值)。
        var (_, data) = Command(CmdFlashMd5, Words((uint)offset, (uint)length, 0, 0),
            TimeSpan.FromSeconds(Math.Max(10, 8.0 * length / (1 << 20))));
        if (data.Length < 32) throw new InvalidDataException("MD5 应答长度异常。");
        return System.Text.Encoding.ASCII.GetString(data, 0, 32).ToLowerInvariant();
    }

    private uint ReadReg(uint address) => Command(CmdReadReg, Words(address)).Value;

    /// <summary>
    /// 依次尝试三种进入下载模式的方式:原生 USB 串口(USB-Serial/JTAG)复位序列、
    /// USB 转串口芯片的经典 DTR/RTS 复位,以及不复位直接同步(用户已按住 BOOT 上电)。
    /// </summary>
    private void Connect(CancellationToken ct)
    {
        _link.BaudRate = RomBaud;
        Action[] strategies = [UsbJtagReset, ClassicReset, () => { }];
        foreach (var reset in strategies)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                reset();
                _link.DiscardInput();
                _decoder.Reset();
                if (TrySync()) return;
            }
        }
        throw new TimeoutException("无法进入下载模式。请按住设备上的 BOOT 键,拔插 USB 后松开,再重试。");
    }

    private bool TrySync()
    {
        var payload = new byte[36];
        payload[0] = 0x07; payload[1] = 0x07; payload[2] = 0x12; payload[3] = 0x20;
        Array.Fill(payload, (byte)0x55, 4, 32);
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Command(CmdSync, payload, TimeSpan.FromMilliseconds(150));
                // ROM 对一次 SYNC 会连续回 8 帧;清掉剩余应答,避免被下一条命令误读。
                _sleep(TimeSpan.FromMilliseconds(50));
                _link.DiscardInput();
                _decoder.Reset();
                return true;
            }
            catch (TimeoutException) { }
            catch (InvalidDataException) { }
        }
        return false;
    }

    /// <summary>esptool USBJTAGSerialReset:经由 (1,1) 而非 (0,0) 过渡;Windows 只在设置 RTS 时下发 DTR。</summary>
    private void UsbJtagReset()
    {
        _link.SetRts(false); _link.SetDtr(false);
        _sleep(TimeSpan.FromMilliseconds(100));
        _link.SetDtr(true); _link.SetRts(false);
        _sleep(TimeSpan.FromMilliseconds(100));
        _link.SetRts(true); _link.SetDtr(false); _link.SetRts(true);
        _sleep(TimeSpan.FromMilliseconds(100));
        _link.SetDtr(false); _link.SetRts(false);
        _sleep(TimeSpan.FromMilliseconds(50));
    }

    /// <summary>esptool ClassicReset:RTS 拉 EN、DTR 拉 IO0,适用于 CP210x/CH340 等转串口芯片。</summary>
    private void ClassicReset()
    {
        _link.SetDtr(false); _link.SetRts(true);
        _sleep(TimeSpan.FromMilliseconds(100));
        _link.SetDtr(true); _link.SetRts(false);
        _sleep(TimeSpan.FromMilliseconds(50));
        _link.SetDtr(false);
    }

    private void HardReset()
    {
        _link.SetDtr(false);
        _link.SetRts(true);
        _sleep(TimeSpan.FromMilliseconds(200));
        _link.SetRts(false);
        _sleep(TimeSpan.FromMilliseconds(200));
    }

    private (uint Value, byte[] Data) Command(byte op, byte[] data, TimeSpan? timeout = null, uint checksum = 0)
    {
        var packet = new byte[8 + data.Length];
        packet[0] = 0x00;
        packet[1] = op;
        BitConverter.TryWriteBytes(packet.AsSpan(2), (ushort)data.Length);
        BitConverter.TryWriteBytes(packet.AsSpan(4), checksum);
        data.CopyTo(packet, 8);
        _link.Write(EspSlip.Encode(packet));

        long deadline = Environment.TickCount64 + (long)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds;
        // ROM 可能先回之前命令(如 SYNC)的余帧;按命令号匹配,最多跳过若干不相干帧。
        for (int skipped = 0; skipped < 32;)
        {
            byte[] frame = ReadFrame(deadline);
            if (frame.Length < 8 || frame[0] != 0x01 || frame[1] != op) { skipped++; continue; }
            int size = BitConverter.ToUInt16(frame, 2);
            if (frame.Length < 8 + size) throw new InvalidDataException("ROM 应答长度不足。");
            uint value = BitConverter.ToUInt32(frame, 4);
            // ESP32 系列 ROM 应答尾部 4 字节状态(stub 为 2 字节):首字节非 0 即失败,次字节为错误码。
            int statusLength = size >= 4 ? 4 : 2;
            if (size < statusLength) throw new InvalidDataException("ROM 应答缺少状态字节。");
            byte status = frame[8 + size - statusLength], error = frame[8 + size - statusLength + 1];
            if (status != 0)
                throw new EspRomException(op, error);
            return (value, frame.AsSpan(8, size - statusLength).ToArray());
        }
        throw new InvalidDataException($"ROM 未回应命令 0x{op:X2}。");
    }

    private byte[] ReadFrame(long deadline)
    {
        while (true)
        {
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new TimeoutException("等待 ROM 应答超时。");
            int b = _link.ReadByte(TimeSpan.FromMilliseconds(remaining));
            if (b < 0) throw new TimeoutException("等待 ROM 应答超时。");
            if (_decoder.Push((byte)b) is { } frame) return frame;
        }
    }

    internal static uint Checksum(ReadOnlySpan<byte> data)
    {
        byte value = 0xEF;
        foreach (byte b in data) value ^= b;
        return value;
    }

    private static byte[] Words(params uint[] words)
    {
        var data = new byte[words.Length * 4];
        for (int i = 0; i < words.Length; i++) BitConverter.TryWriteBytes(data.AsSpan(i * 4), words[i]);
        return data;
    }
}

public sealed class EspRomException(byte command, byte error) : Exception(
    $"设备拒绝命令 0x{command:X2}(错误码 0x{error:X2}{Describe(error)})。")
{
    public byte Command { get; } = command;
    public byte Error { get; } = error;

    private static string Describe(byte error) => error switch
    {
        0x05 => ",收到的消息无效",
        0x06 => ",执行失败",
        0x07 => ",CRC/校验和错误",
        0x08 => ",Flash 写入失败",
        0x09 => ",Flash 读取失败",
        0x0A => ",读取长度错误",
        0x0B => ",解压失败",
        _ => "",
    };
}
