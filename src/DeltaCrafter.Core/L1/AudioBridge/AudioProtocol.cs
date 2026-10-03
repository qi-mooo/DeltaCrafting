using System.Buffers.Binary;

namespace DeltaCrafter.Core.L1.AudioBridge;

public sealed record AudioGainState(bool Muted, float LinearGain, string DeviceId);

public static class AudioProtocol
{
    public const byte Version = 3;
    public const int DiscoveryPort = 40100;
    public const int AudioPort = 40101;
    public const int LevelPort = 40102;
    public const byte LevelMagic0 = (byte)'T';
    public const byte LevelMagic1 = (byte)'L';
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int FramesPerChunk = 240;
    public const int ChunkBytes = FramesPerChunk * Channels * sizeof(short);

    public static byte[] CreateLevelPacket(byte level) =>
        new[] { LevelMagic0, LevelMagic1, Version, Math.Min(level, (byte)100) };

    public static byte[] CreatePcmChunk(ReadOnlySpan<short> samples)
    {
        if (samples.Length != FramesPerChunk * Channels)
        {
            throw new ArgumentException($"Expected {FramesPerChunk * Channels} samples.", nameof(samples));
        }

        var chunk = new byte[ChunkBytes];
        var payload = chunk.AsSpan();
        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload.Slice(index * sizeof(short), sizeof(short)), samples[index]);
        }
        return chunk;
    }
}

public sealed class PcmStreamChunker
{
    private readonly short[] _samples = new short[AudioProtocol.FramesPerChunk * AudioProtocol.Channels];
    private int _sampleCount;

    public void Reset()
    {
        _sampleCount = 0;
    }

    public IReadOnlyList<byte[]> AddSamples(
        float[] source,
        int offset,
        int count,
        AudioGainState gainState)
    {
        if (offset < 0 || count < 0 || offset + count > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        List<byte[]>? chunks = null;
        var gain = gainState.Muted ? 0.0f : gainState.LinearGain;

        for (var index = 0; index < count; index++)
        {
            var scaled = Math.Clamp(source[offset + index] * gain, -1.0f, 1.0f);
            _samples[_sampleCount++] = scaled <= -1.0f
                ? short.MinValue
                : (short)MathF.Round(scaled * short.MaxValue);

            if (_sampleCount != _samples.Length)
            {
                continue;
            }

            chunks ??= new List<byte[]>();
            chunks.Add(AudioProtocol.CreatePcmChunk(_samples));
            _sampleCount = 0;
        }

        return chunks is null ? Array.Empty<byte[]>() : chunks;
    }
}
