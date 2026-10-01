using System.Buffers.Binary;
using DeltaCrafter.Core.L1.AudioBridge;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class AudioProtocolTests
{
    [Theory]
    [InlineData(42, 42)]
    [InlineData(255, 100)]
    public void CreateLevelPacketUsesFixedHeaderAndClampsLevel(byte input, byte expected)
    {
        var packet = AudioProtocol.CreateLevelPacket(input);

        Assert.Equal(new byte[]
        {
            AudioProtocol.LevelMagic0,
            AudioProtocol.LevelMagic1,
            AudioProtocol.Version,
            expected,
        }, packet);
    }

    [Fact]
    public void CreatePcmChunkWritesLittleEndianMonoSamples()
    {
        var samples = new short[AudioProtocol.FramesPerChunk * AudioProtocol.Channels];
        samples[0] = 0x1234;
        samples[1] = -2;

        var chunk = AudioProtocol.CreatePcmChunk(samples);

        Assert.Equal(AudioProtocol.ChunkBytes, chunk.Length);
        Assert.Equal(0x1234, BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan(0, 2)));
        Assert.Equal(-2, BinaryPrimitives.ReadInt16LittleEndian(chunk.AsSpan(2, 2)));
    }

    [Fact]
    public void ChunkerCarriesPartialFramesAndAppliesGain()
    {
        var chunker = new PcmStreamChunker();
        var first = Enumerable.Repeat(0.5f, 100).ToArray();
        var second = Enumerable.Repeat(0.5f, 380).ToArray();
        var gain = new AudioGainState(false, 0.5f, "device");

        Assert.Empty(chunker.AddSamples(first, 0, first.Length, gain));
        var chunks = chunker.AddSamples(second, 0, second.Length, gain);

        Assert.Equal(2, chunks.Count);
        Assert.InRange(BinaryPrimitives.ReadInt16LittleEndian(chunks[0].AsSpan(0, 2)), 8190, 8192);
    }

    [Fact]
    public void MutedChunksContainSilence()
    {
        var chunker = new PcmStreamChunker();
        var source = Enumerable.Repeat(1.0f, AudioProtocol.FramesPerChunk).ToArray();
        var chunk = Assert.Single(chunker.AddSamples(
            source,
            0,
            source.Length,
            new AudioGainState(true, 1.0f, "device")));

        Assert.All(chunk, value => Assert.Equal(0, value));
    }
}
