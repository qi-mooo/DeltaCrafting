using NAudio.Wave;

namespace DeltaCrafter.Core.L1.AudioBridge;

public sealed class MonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _sourceBuffer = Array.Empty<float>();

    public MonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        var requiredSamples = count * _sourceChannels;
        if (_sourceBuffer.Length < requiredSamples)
        {
            _sourceBuffer = new float[requiredSamples];
        }

        var sourceSamples = _source.Read(_sourceBuffer, 0, requiredSamples);
        var frames = sourceSamples / _sourceChannels;
        for (var frame = 0; frame < frames; frame++)
        {
            var sourceOffset = frame * _sourceChannels;
            float sample;
            if (_sourceChannels == 1)
            {
                sample = _sourceBuffer[sourceOffset];
            }
            else
            {
                // A 0.5/0.5 stereo downmix preserves headroom for correlated material
                // and avoids the hard clipping that degrades speech and music quality.
                sample = (_sourceBuffer[sourceOffset] + _sourceBuffer[sourceOffset + 1]) * 0.5f;
            }
            buffer[offset + frame] = Math.Clamp(sample, -1.0f, 1.0f);
        }
        return frames;
    }
}
