namespace DeltaCrafter.Core.L0;

public sealed record AudioState(bool Muted, int VolumePercent, float VolumeDb,
    string DeviceId, DateTimeOffset Timestamp);

public sealed record AudioBridgeStatus(AudioState? Audio, string Error, string BridgeError)
{
    public string Label => Audio is null ? "声音不可用" : Audio.Muted ? "已静音" : "声音正常";
    public string Glyph => Audio is null ? "\uE7BA" : Audio.Muted ? "\uE74F" : "\uE767";
    public string Hint => Audio is null ? Error
        : (Audio.Muted ? "取消静音" : "静音") + $" · 音量 {Audio.VolumePercent}%"
          + (BridgeError.Length > 0 ? " · " + BridgeError : "");
}

public interface IWindowsAudio
{
    AudioState GetState();
    AudioState SetMute(bool muted);
    AudioState ToggleMute();
    AudioState SetVolume(int volumePercent);
    AudioState ChangeVolume(int deltaPercent);
}

public sealed class AudioDeviceUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
