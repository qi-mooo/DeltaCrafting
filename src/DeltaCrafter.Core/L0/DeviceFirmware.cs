namespace DeltaCrafter.Core.L0;

public enum DeviceFirmwareTarget { Display, Harp }

public sealed record DeviceFirmwareImage(string Role, string File, int Address, int Size, string Sha256);

public sealed record DeviceFirmwareManifest(int SchemaVersion, string Board, string Layout,
    string FirmwareVersion, int Revision, int MinimumUpdater, IReadOnlyList<DeviceFirmwareImage> Images)
{
    public string BundleId { get; init; } = "";

    public void Validate()
    {
        if (Board == "esp32-s3-dongle-fn8")
        {
            if (SchemaVersion != 1 || Layout != "dual-8mb-v1" || Revision < 2 || MinimumUpdater != 1
                || FirmwareVersion != $"delta-harp-v{Revision}" || Images is null || Images.Count != 1
                || Images[0] is not { Role: "player", File: "firmware.bin", Address: 0x10000, Size: >= 288 and <= 0x330000 } image
                || !ValidHash(image.Sha256)) throw new InvalidDataException("不兼容的 Harp USB 固件包。");
            return;
        }
        if (SchemaVersion != 1 || Board != "lilygo-t-display-s3" || Layout != "dual-16mb-v1"
            || Revision < 16 || MinimumUpdater != 1 || FirmwareVersion != $"axeuh-tools-v{Revision}"
            || Images is null || Images.Count != 2)
            throw new InvalidDataException("不兼容的 S3 固件包。");
        foreach (var (role, file, address) in new[] { ("monitor", "firmware.bin", 0x10000), ("audio", "audio-firmware.bin", 0x650000) })
        {
            var matching = Images.Where(i => i is not null && i.Role == role).ToArray();
            if (matching.Length != 1) throw new InvalidDataException("固件映像缺失或重复。");
            var image = matching[0];
            if (image.File != file || image.Address != address
                || image.Size is < 32 or > 0x640000 || !ValidHash(image.Sha256))
                throw new InvalidDataException("固件映像或分区信息无效。");
        }
    }

    public static bool ValidHash(string? hash) => hash is { Length: 64 }
        && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// The USB bundle uses the same manifest as its direct OTA command-line tool.
public sealed record HarpFirmwareBundle(int SchemaVersion, string Board, string Layout,
    string Version, int Size, string Sha256)
{
    public DeviceFirmwareManifest ToDeviceManifest()
    {
        const string prefix = "delta-harp-v";
        if (Version is null || !Version.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(Version[prefix.Length..], out int revision) || Board != "esp32-s3-dongle-fn8")
            throw new InvalidDataException("无效的 Harp 固件版本或板型。");
        var manifest = new DeviceFirmwareManifest(SchemaVersion, Board, Layout, Version, revision, 1,
            [new("player", "firmware.bin", 0x10000, Size, Sha256)]);
        manifest.Validate();
        return manifest;
    }
}
