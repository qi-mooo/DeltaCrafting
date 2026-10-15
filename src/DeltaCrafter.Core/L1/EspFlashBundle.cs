using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DeltaCrafter.Core.L1;

public sealed record EspFlashImage(int Offset, string File, byte[] Data);

/// <summary>
/// 固件包里的 flash-manifest.json:整片 USB 刷写所需的全部映像(引导程序、分区表、
/// boot_app0、应用)及偏移。与 flash.py 一致,只写映像覆盖的扇区,其余扇区(NVS 配置)保留。
/// </summary>
public sealed record EspFlashBundle(string FirmwareVersion, string FlashMode, string FlashFreq,
    string FlashSize, IReadOnlyList<EspFlashImage> Images)
{
    private const int MaxImageBytes = 0x800000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>ESP32-S3 引导程序位于 0x0;只有它的映像头需要写入实际的 Flash 参数。</summary>
    public const int BootloaderOffset = 0x0;

    /// <summary>容量编码高 4 位为 log2(MB):0x00=1MB … 0x70=128MB。</summary>
    public int FlashSizeBytes => 1 << (20 + (FlashSizeCode(FlashSize) >> 4));

    public static EspFlashBundle Load(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = JsonSerializer.Deserialize<Manifest>(ReadEntry(zip, "flash-manifest.json", 16384), Json)
            ?? throw new InvalidDataException("固件包缺少刷写清单。");
        if (manifest.Chip != "esp32s3")
            throw new InvalidDataException($"固件包面向 {manifest.Chip},只支持 ESP32-S3。");
        if (manifest.Images is not { Count: > 0 })
            throw new InvalidDataException("刷写清单没有映像。");
        // 参数在加载时即校验,避免写到一半才发现无法编码进映像头。
        FlashModeCode(manifest.FlashMode);
        FlashFreqCode(manifest.FlashFreq);
        FlashSizeCode(manifest.FlashSize);

        var images = new List<EspFlashImage>();
        foreach (var entry in manifest.Images)
        {
            if (entry.File is null || entry.File.Contains('/') || entry.File.Contains('\\'))
                throw new InvalidDataException("刷写清单含非法文件名。");
            byte[] data = ReadEntry(zip, entry.File, MaxImageBytes);
            if (Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != entry.Sha256)
                throw new InvalidDataException("固件映像校验失败: " + entry.File);
            images.Add(new(ParseOffset(entry.Offset), entry.File, data));
        }
        var bundle = new EspFlashBundle(manifest.FirmwareVersion ?? "", manifest.FlashMode!, manifest.FlashFreq!,
            manifest.FlashSize!, images.OrderBy(i => i.Offset).ToArray());
        bundle.Validate();
        return bundle;
    }

    private void Validate()
    {
        int flashBytes = FlashSizeBytes;
        for (int i = 0; i < Images.Count; i++)
        {
            var image = Images[i];
            if (image.Offset % 0x1000 != 0)
                throw new InvalidDataException($"映像 {image.File} 偏移 0x{image.Offset:X} 未按 4 KB 扇区对齐。");
            if ((long)image.Offset + image.Data.Length > flashBytes)
                throw new InvalidDataException($"映像 {image.File} 超出 {FlashSize} Flash。");
            if (i > 0 && Images[i - 1].Offset + Images[i - 1].Data.Length > image.Offset)
                throw new InvalidDataException($"映像 {Images[i - 1].File} 与 {image.File} 重叠。");
        }
        if (!Images.Any(i => i.Offset == BootloaderOffset))
            throw new InvalidDataException("刷写清单缺少 0x0 处的引导程序。");
    }

    /// <summary>
    /// 返回实际写入的字节。引导程序映像头第 2、3 字节写入清单给出的 Flash 模式/频率/容量
    /// (预编译引导程序头里是通用默认值),若映像末尾附带 SHA-256 则同步重算——
    /// 与 esptool write_flash 的 _update_image_flash_params 行为一致。其余映像原样写入。
    /// </summary>
    public byte[] PayloadFor(EspFlashImage image)
    {
        if (image.Offset != BootloaderOffset) return image.Data;
        var data = (byte[])image.Data.Clone();
        if (data.Length < 24 || data[0] != 0xE9)
            throw new InvalidDataException("引导程序不是有效的 ESP 映像。");
        if (data[12] != 9 || data[13] != 0)
            throw new InvalidDataException("引导程序不是 ESP32-S3 映像。");
        byte mode = FlashModeCode(FlashMode);
        byte sizeFreq = (byte)(FlashSizeCode(FlashSize) | FlashFreqCode(FlashFreq));
        if (data[2] == mode && data[3] == sizeFreq) return data;

        // 先在原映像上确认附带摘要有效,再改头;摘要本就不符时不擅自"修复"。
        int dataLength = ImageDataLength(data);
        bool rehash = data[23] == 1 && data.Length >= dataLength + 32
            && SHA256.HashData(data.AsSpan(0, dataLength)).AsSpan().SequenceEqual(data.AsSpan(dataLength, 32));
        data[2] = mode;
        data[3] = sizeFreq;
        if (rehash) SHA256.HashData(data.AsSpan(0, dataLength), data.AsSpan(dataLength, 32));
        return data;
    }

    /// <summary>映像头(8)+扩展头(16)+各段,校验和补齐到 16 字节边界;SHA-256 覆盖到此为止。</summary>
    internal static int ImageDataLength(byte[] data)
    {
        int position = 24;
        for (int i = 0; i < data[1]; i++)
        {
            if (position + 8 > data.Length) throw new InvalidDataException("映像段表越界。");
            int length = BitConverter.ToInt32(data, position + 4);
            if (length < 0 || position + 8L + length > data.Length) throw new InvalidDataException("映像段长度越界。");
            position += 8 + length;
        }
        int dataLength = (position / 16 + 1) * 16;
        if (dataLength > data.Length) throw new InvalidDataException("映像缺少校验和。");
        return dataLength;
    }

    internal static byte FlashModeCode(string? mode) => mode switch
    {
        "qio" => 0, "qout" => 1, "dio" => 2, "dout" => 3,
        _ => throw new InvalidDataException("不支持的 Flash 模式: " + mode),
    };

    internal static byte FlashFreqCode(string? freq) => freq switch
    {
        "80m" => 0xF, "40m" => 0x0, "26m" => 0x1, "20m" => 0x2,
        _ => throw new InvalidDataException("不支持的 Flash 频率: " + freq),
    };

    internal static byte FlashSizeCode(string? size) => size switch
    {
        "1MB" => 0x00, "2MB" => 0x10, "4MB" => 0x20, "8MB" => 0x30,
        "16MB" => 0x40, "32MB" => 0x50, "64MB" => 0x60, "128MB" => 0x70,
        _ => throw new InvalidDataException("不支持的 Flash 容量: " + size),
    };

    private static int ParseOffset(string? text)
    {
        if (text is null || !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(text.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out int offset)
            || offset < 0)
            throw new InvalidDataException("刷写清单偏移无效: " + text);
        return offset;
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, int maximum)
    {
        var entries = zip.Entries.Where(e => e.FullName == name).ToArray();
        if (entries.Length != 1 || entries[0].Length < 1 || entries[0].Length > maximum)
            throw new InvalidDataException("固件文件缺失、重复或过大: " + name);
        using var input = entries[0].Open();
        byte[] data = new byte[(int)entries[0].Length];
        input.ReadExactly(data);
        return data;
    }

    private sealed record Manifest(string? Chip, string? FirmwareVersion, string? FlashMode, string? FlashFreq,
        string? FlashSize, IReadOnlyList<ManifestImage>? Images);

    private sealed record ManifestImage(string? Offset, string? File, string? Sha256);
}
