using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class EspFlashTests
{
    [Fact]
    public void Slip_round_trips_reserved_bytes_and_skips_noise_before_a_frame()
    {
        byte[] payload = [0x00, 0xC0, 0x01, 0xDB, 0xC0, 0xDB, 0x7F];
        var decoder = new EspSlip.Decoder();
        byte[]? frame = null;
        foreach (byte b in "ESP-ROM:esp32s3\r\n"u8.ToArray().Concat(EspSlip.Encode(payload)))
            frame ??= decoder.Push(b);
        Assert.Equal(payload, frame);
    }

    [Fact]
    public void Bootloader_header_gets_manifest_flash_params_and_a_recomputed_digest()
    {
        byte[] bootloader = Bootloader(mode: 0, sizeFreq: 0x2F);
        var bundle = LoadBundle(("0x0", "bootloader.bin", bootloader), ("0x10000", "firmware.bin", App(5000)));

        byte[] patched = bundle.PayloadFor(bundle.Images[0]);
        int dataLength = EspFlashBundle.ImageDataLength(patched);
        Assert.Equal(2, patched[2]);                 // dio
        Assert.Equal(0x4F, patched[3]);              // 16MB | 80m
        Assert.Equal(SHA256.HashData(patched.AsSpan(0, dataLength)), patched.AsSpan(dataLength, 32).ToArray());
        Assert.Equal(0, bootloader[2]);              // 原始包内数据不被修改
        Assert.Same(bundle.Images[1].Data, bundle.PayloadFor(bundle.Images[1]));
    }

    [Fact]
    public void Bundle_rejects_overlapping_images_and_missing_bootloader()
    {
        Assert.Throws<InvalidDataException>(() => LoadBundle(
            ("0x0", "bootloader.bin", Bootloader(2, 0x4F)), ("0x10000", "firmware.bin", App(0x8000)),
            ("0x14000", "audio-firmware.bin", App(0x1000))));
        Assert.Throws<InvalidDataException>(() => LoadBundle(("0x10000", "firmware.bin", App(100))));
    }

    [Fact]
    public void Flashes_every_image_compressed_and_verifies_md5_against_a_simulated_rom()
    {
        var bundle = LoadBundle(("0x0", "bootloader.bin", Bootloader(0, 0x2F)),
            ("0x8000", "partitions.bin", Random(3072, 1)), ("0x10000", "firmware.bin", App(300_000)));
        var rom = new FakeEspRom();
        var reports = new List<EspFlashProgress>();

        new EspRomFlasher(rom, sleep: _ => { }).Flash(bundle, new SyncProgress(reports.Add), CancellationToken.None);

        foreach (var image in bundle.Images)
        {
            byte[] expected = bundle.PayloadFor(image);
            Assert.Equal(expected, rom.Flash.AsSpan(image.Offset, expected.Length).ToArray());
        }
        Assert.Equal(16 << 20, rom.FlashSizeParam);
        Assert.Equal(460800, rom.BaudRate);
        Assert.Equal(1, reports[^1].Fraction);
        Assert.True(rom.ResetPulsed);
    }

    [Fact]
    public void Wrong_chip_and_corrupted_writes_are_reported()
    {
        var bundle = LoadBundle(("0x0", "bootloader.bin", Bootloader(2, 0x4F)), ("0x10000", "firmware.bin", App(8000)));

        var esp32 = new FakeEspRom { ChipMagic = 0x00F01D83 };
        Assert.Contains("不是 ESP32-S3", Assert.Throws<InvalidOperationException>(() =>
            new EspRomFlasher(esp32, sleep: _ => { }).Flash(bundle, null, CancellationToken.None)).Message);

        var flaky = new FakeEspRom { CorruptOffset = 0x10010 };
        Assert.Contains("校验不一致", Assert.Throws<InvalidOperationException>(() =>
            new EspRomFlasher(flaky, sleep: _ => { }).Flash(bundle, null, CancellationToken.None)).Message);
    }

    [Fact]
    public void Silent_port_fails_with_boot_button_instructions()
    {
        var bundle = LoadBundle(("0x0", "bootloader.bin", Bootloader(2, 0x4F)));
        var silent = new FakeEspRom { Responsive = false };
        Assert.Contains("BOOT", Assert.Throws<TimeoutException>(() =>
            new EspRomFlasher(silent, sleep: _ => { }).Flash(bundle, null, CancellationToken.None)).Message);
    }

    // ---------- 测试数据 ----------

    private static EspFlashBundle LoadBundle(params (string Offset, string File, byte[] Data)[] images)
    {
        string path = Path.Combine(Path.GetTempPath(), $"esp-bundle-{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var image in images)
                    using (var s = zip.CreateEntry(image.File).Open()) s.Write(image.Data);
                var manifest = new
                {
                    chip = "esp32s3", firmwareVersion = "test-v1", flashMode = "dio", flashFreq = "80m", flashSize = "16MB",
                    images = images.Select(i => new
                    {
                        offset = i.Offset, file = i.File,
                        sha256 = Convert.ToHexString(SHA256.HashData(i.Data)).ToLowerInvariant(),
                    }),
                };
                using var m = zip.CreateEntry("flash-manifest.json").Open();
                JsonSerializer.Serialize(m, manifest);
            }
            return EspFlashBundle.Load(path);
        }
        finally { File.Delete(path); }
    }

    /// <summary>最小但结构完整的 ESP32-S3 引导程序:头 + 扩展头 + 2 段 + 校验和 + SHA-256。</summary>
    private static byte[] Bootloader(byte mode, byte sizeFreq)
    {
        var image = new List<byte> { 0xE9, 2, mode, sizeFreq, 0x00, 0x00, 0x3C, 0x40 };
        var ext = new byte[16];
        ext[4] = 9;      // chip id = ESP32-S3
        ext[15] = 1;     // hash appended
        image.AddRange(ext);
        byte checksum = 0xEF;
        foreach (int length in new[] { 37, 100 })
        {
            image.AddRange(BitConverter.GetBytes(0x3FCE0000u));
            image.AddRange(BitConverter.GetBytes(length));
            byte[] data = Random(length, length);
            foreach (byte b in data) checksum ^= b;
            image.AddRange(data);
        }
        while ((image.Count + 1) % 16 != 0) image.Add(0);
        image.Add(checksum);
        image.AddRange(SHA256.HashData(image.ToArray()));
        return image.ToArray();
    }

    private static byte[] App(int length)
    {
        // 带重复片段,让压缩与多块传输都被覆盖。
        var data = Random(length, 7);
        for (int i = 0; i + 64 < length; i += 256) Array.Fill(data, (byte)0xAA, i, 64);
        data[0] = 0xE9;
        return data;
    }

    private static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private sealed class SyncProgress(Action<EspFlashProgress> report) : IProgress<EspFlashProgress>
    {
        public void Report(EspFlashProgress value) => report(value);
    }

    /// <summary>模拟 ESP32-S3 ROM 下载模式:解析 SLIP 命令帧,维护 16 MB 虚拟 Flash。</summary>
    private sealed class FakeEspRom : IEspSerialLink
    {
        public byte[] Flash { get; } = Enumerable.Repeat((byte)0xFF, 16 << 20).ToArray();
        public uint ChipMagic { get; init; } = EspRomFlasher.Esp32S3Magic;
        public int? CorruptOffset { get; init; }
        public bool Responsive { get; init; } = true;
        public int FlashSizeParam { get; private set; }
        public bool ResetPulsed { get; private set; }
        public int BaudRate { get; set; }

        private readonly Queue<byte> _output = new();
        private readonly EspSlip.Decoder _decoder = new();
        private MemoryStream? _compressed;
        private int _writeOffset, _nextSeq;
        private bool _rts;

        public void Write(byte[] data)
        {
            if (!Responsive) return;
            foreach (byte b in data)
                if (_decoder.Push(b) is { } frame) Handle(frame);
        }

        public int ReadByte(TimeSpan timeout) => _output.Count > 0 ? _output.Dequeue() : -1;
        public void DiscardInput() => _output.Clear();
        public void SetDtr(bool value) { }
        // 只记录写入完成后的复位脉冲(连接阶段的复位序列不算)。
        public void SetRts(bool value) { if (_rts && !value && _nextSeq > 0) ResetPulsed = true; _rts = value; }
        public void Dispose() { }

        private void Handle(byte[] frame)
        {
            byte op = frame[1];
            int size = BitConverter.ToUInt16(frame, 2);
            uint checksum = BitConverter.ToUInt32(frame, 4);
            byte[] data = frame.AsSpan(8, size).ToArray();
            uint Word(int i) => BitConverter.ToUInt32(data, i * 4);
            switch (op)
            {
                case 0x08:
                    for (int i = 0; i < 8; i++) Reply(op);
                    break;
                case 0x0A:
                    Reply(op, Word(0) == EspRomFlasher.ChipDetectRegister ? ChipMagic : 0);
                    break;
                case 0x0B:
                    FlashSizeParam = (int)Word(1);
                    Reply(op);
                    break;
                case 0x0D:
                    Reply(op, error: size == 8 ? (byte)0 : (byte)0x05);
                    break;
                case 0x0F:
                    Reply(op);
                    break;
                case 0x10:
                    Commit();
                    if (size != 20) { Reply(op, error: 0x05); break; }
                    _writeOffset = (int)Word(3);
                    Array.Fill(Flash, (byte)0xFF, _writeOffset, (int)Word(0));
                    _compressed = new MemoryStream();
                    _nextSeq = 0;
                    Reply(op);
                    break;
                case 0x11:
                {
                    var block = data.AsSpan(16);
                    bool ok = _compressed is not null && Word(0) == block.Length && Word(1) == _nextSeq
                        && checksum == EspRomFlasher.Checksum(block);
                    if (ok) { _compressed!.Write(block); _nextSeq++; }
                    Reply(op, error: ok ? (byte)0 : (byte)0x07);
                    break;
                }
                case 0x13:
                {
                    Commit();
                    var md5 = MD5.HashData(Flash.AsSpan((int)Word(0), (int)Word(1)));
                    Reply(op, payload: Encoding.ASCII.GetBytes(Convert.ToHexString(md5)));
                    break;
                }
                default:
                    Reply(op, error: 0x05);
                    break;
            }
        }

        private void Commit()
        {
            if (_compressed is null) return;
            _compressed.Position = 0;
            using var zlib = new ZLibStream(_compressed, CompressionMode.Decompress);
            using var plain = new MemoryStream();
            zlib.CopyTo(plain);
            plain.ToArray().CopyTo(Flash, _writeOffset);
            if (CorruptOffset is { } bad && bad >= _writeOffset && bad < _writeOffset + plain.Length) Flash[bad] ^= 0xFF;
            _compressed = null;
        }

        private void Reply(byte op, uint value = 0, byte[]? payload = null, byte error = 0)
        {
            payload ??= Array.Empty<byte>();
            var body = new byte[8 + payload.Length + 4];
            body[0] = 0x01;
            body[1] = op;
            BitConverter.TryWriteBytes(body.AsSpan(2), (ushort)(payload.Length + 4));
            BitConverter.TryWriteBytes(body.AsSpan(4), value);
            payload.CopyTo(body, 8);
            body[8 + payload.Length] = error == 0 ? (byte)0 : (byte)1;
            body[8 + payload.Length + 1] = error;
            foreach (byte b in EspSlip.Encode(body)) _output.Enqueue(b);
        }
    }
}
