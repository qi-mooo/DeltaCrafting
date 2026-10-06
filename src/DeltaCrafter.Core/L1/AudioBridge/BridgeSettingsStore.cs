using System.Text.Json;

namespace DeltaCrafter.Core.L1.AudioBridge;

public sealed class BridgeSettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private BridgeSettings _settings;

    public BridgeSettingsStore(string root, string? legacyPath = null)
    {
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "audio-bridge.json");
        if (!File.Exists(_path) && legacyPath is not null && File.Exists(legacyPath))
        {
            // Read before copying so a bad legacy file cannot poison the new store.
            var legacy = Read(legacyPath);
            new JsonStoreBrick().Save(_path, legacy);
        }
        _settings = Read(_path);
    }

    public string? SelectedDeviceId
    {
        get { lock (_gate) return _settings.SelectedDeviceId; }
        set
        {
            lock (_gate)
            {
                var next = new BridgeSettings(value);
                new JsonStoreBrick().Save(_path, next);
                _settings = next;
            }
        }
    }

    private static BridgeSettings Read(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
        : new();
}

public sealed record BridgeSettings(string? SelectedDeviceId = null);
