using System.Runtime.InteropServices;
using DeltaCrafter.Core.L0;
using NAudio.CoreAudioApi;

namespace DeltaCrafter.Core.L1.AudioBridge;

public sealed class WindowsAudioService : IWindowsAudio
{
    private readonly object _gate = new();

    public AudioState GetState() => WithEndpoint(ReadState);
    public AudioState SetMute(bool muted) => WithEndpoint(device =>
    {
        device.AudioEndpointVolume.Mute = muted;
        return ReadState(device);
    });
    public AudioState ToggleMute() => WithEndpoint(device =>
    {
        var volume = device.AudioEndpointVolume;
        volume.Mute = !volume.Mute;
        return ReadState(device);
    });
    public AudioState SetVolume(int percent) => WithEndpoint(device =>
    {
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
        return ReadState(device);
    });
    public AudioState ChangeVolume(int delta) => WithEndpoint(device =>
    {
        var volume = device.AudioEndpointVolume;
        volume.MasterVolumeLevelScalar = Math.Clamp(volume.MasterVolumeLevelScalar + delta / 100f, 0, 1);
        return ReadState(device);
    });
    public AudioGainState GetGainState()
    {
        var state = GetState();
        return new(state.Muted, state.Muted ? 0 : Math.Clamp(MathF.Pow(10, state.VolumeDb / 20), 0, 1), state.DeviceId);
    }

    private static AudioState ReadState(MMDevice device)
    {
        var volume = device.AudioEndpointVolume;
        return new(volume.Mute, Math.Clamp((int)Math.Round(volume.MasterVolumeLevelScalar * 100), 0, 100),
            volume.MasterVolumeLevel, device.ID, DateTimeOffset.UtcNow);
    }

    private T WithEndpoint<T>(Func<MMDevice, T> operation)
    {
        lock (_gate)
        {
            try
            {
                // Use the same COM interface definitions as WASAPI capture. The old
                // service mixed hand-declared interfaces and forced RCW releases.
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return operation(device);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                throw new AudioDeviceUnavailableException("无法访问 Windows 默认扬声器。", ex);
            }
        }
    }
}
