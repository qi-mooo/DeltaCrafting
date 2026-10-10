using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1;

/// <summary>Validated, immutable bundles keep an interrupted device update pinned to its original images.</summary>
public sealed class DeviceFirmwareStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly DeviceFirmwareTarget _target;
    private readonly object _gate = new();
    private DeviceFirmwareManifest? _current;
    public DeviceFirmwareManifest? Current => Volatile.Read(ref _current);

    public DeviceFirmwareStore(string root, DeviceFirmwareTarget target = DeviceFirmwareTarget.Display)
    {
        _root = root;
        _target = target;
        Directory.CreateDirectory(root);
        string pointer = Path.Combine(root, "current.json");
        if (File.Exists(pointer))
        {
            try
            {
                string? id = JsonSerializer.Deserialize<string>(File.ReadAllText(pointer));
                if (DeviceFirmwareManifest.ValidHash(id)) _current = ReadManifest(id!);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
        }
    }

    public DeviceFirmwareManifest Import(string zipPath, bool onlyNewer = false)
    {
        lock (_gate)
        {
            using var zip = ZipFile.OpenRead(zipPath);
            byte[] manifestBytes = ReadEntry(zip, "ota-manifest.json", 4096);
            var manifest = Parse(manifestBytes);
            string id = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            manifest = manifest with { BundleId = id };
            var files = new Dictionary<string, byte[]>();
            foreach (var image in manifest.Images)
            {
                byte[] data = ReadEntry(zip, image.File, 0x640000);
                ValidateImage(image, data);
                files.Add(image.File, data);
            }
            if (onlyNewer && Current is { } current && current.Revision >= manifest.Revision) return current;
            string target = Path.Combine(_root, id);
            if (!Directory.Exists(target))
            {
                string stage = Path.Combine(_root, ".stage-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stage);
                try
                {
                    File.WriteAllBytes(Path.Combine(stage, "ota-manifest.json"), manifestBytes);
                    foreach (var file in files) File.WriteAllBytes(Path.Combine(stage, file.Key), file.Value);
                    Directory.Move(stage, target);
                }
                finally { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
            }
            // Revalidate existing content before publishing, including after a previous interrupted import.
            manifest = ReadManifest(id);
            foreach (var image in manifest.Images)
                ValidateImage(image, File.ReadAllBytes(Path.Combine(target, image.File)));
            new JsonStoreBrick().Save(Path.Combine(_root, "current.json"), id);
            Volatile.Write(ref _current, manifest);
            return manifest;
        }
    }

    public Stream? OpenImage(string bundleId, string role)
    {
        if (!DeviceFirmwareManifest.ValidHash(bundleId)
            || (_target == DeviceFirmwareTarget.Harp ? role != "player" : role is not ("monitor" or "audio"))) return null;
        try
        {
            var manifest = ReadManifest(bundleId);
            var image = manifest.Images.Single(i => i.Role == role);
            var stream = new FileStream(Path.Combine(_root, bundleId, image.File), FileMode.Open,
                FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length == image.Size) return stream;
            stream.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { return null; }
    }

    private DeviceFirmwareManifest ReadManifest(string id)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(_root, id, "ota-manifest.json"));
        if (bytes.Length > 4096 || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != id)
            throw new InvalidDataException("固件清单校验失败。");
        return Parse(bytes) with { BundleId = id };
    }

    private DeviceFirmwareManifest Parse(byte[] bytes)
    {
        var manifest = _target == DeviceFirmwareTarget.Harp
            ? JsonSerializer.Deserialize<HarpFirmwareBundle>(bytes, Json)?.ToDeviceManifest()
            : JsonSerializer.Deserialize<DeviceFirmwareManifest>(bytes, Json);
        manifest = manifest
            ?? throw new InvalidDataException("缺少固件清单。");
        if (_target == DeviceFirmwareTarget.Display && manifest.Board != "lilygo-t-display-s3")
            throw new InvalidDataException("此处只接受 T-Display 固件包。");
        manifest.Validate();
        return manifest;
    }

    private static byte[] ReadEntry(ZipArchive zip, string name, int maximum)
    {
        var entries = zip.Entries.Where(e => e.FullName == name).ToArray();
        if (entries.Length != 1 || entries[0].Length is < 1 || entries[0].Length > maximum)
            throw new InvalidDataException("固件文件缺失、重复或过大: " + name);
        using var input = entries[0].Open();
        byte[] data = new byte[(int)entries[0].Length];
        input.ReadExactly(data);
        if (input.ReadByte() != -1) throw new InvalidDataException("固件文件长度异常。");
        return data;
    }

    private static void ValidateImage(DeviceFirmwareImage image, byte[] data)
    {
        // ESP image header: magic + ESP32-S3 chip ID. IDF performs full image verification on the device.
        if (data.Length != image.Size || data[0] != 0xe9 || data[12] != 9 || data[13] != 0
            || (image.Role == "player" && data.AsSpan().IndexOf("DeltaHarp:esp32-s3-dongle-fn8:dual-8mb-v1:app"u8) < 0)
            || Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() != image.Sha256)
            throw new InvalidDataException("固件映像校验失败: " + image.Role);
    }
}
