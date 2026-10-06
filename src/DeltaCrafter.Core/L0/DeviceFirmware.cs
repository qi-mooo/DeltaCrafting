namespace DeltaCrafter.Core.L0;

public sealed record DeviceFirmwareImage(string Role, string File, int Address, int Size, string Sha256);

public sealed record DeviceFirmwareManifest(int SchemaVersion, string Board, string Layout,
    string FirmwareVersion, int Revision, int MinimumUpdater, IReadOnlyList<DeviceFirmwareImage> Images)
{
    public string BundleId { get; init; } = "";

    public void Validate()
    {
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
