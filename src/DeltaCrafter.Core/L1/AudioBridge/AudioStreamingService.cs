using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using DeltaCrafter.Core.L0;

namespace DeltaCrafter.Core.L1.AudioBridge;

public sealed record AudioLevelDelivery(DateTimeOffset? SentAt, byte PeakLevel, IReadOnlyList<string> Targets);

public sealed class AudioStreamingService : IAsyncDisposable
{
    private readonly DeviceDiscoveryService _discovery;
    private readonly WindowsAudioService _windowsAudio;
    private readonly CancellationTokenSource _stop = new();
    private readonly UdpClient _levelUdp = new(AddressFamily.InterNetwork);
    private Task? _runTask;
    private volatile bool _paused;
    private long _sentPackets;
    private long _sentLevelPackets;
    private string _status = "Starting";
    private string _endpointName = "No playback device";
    private string _captureFormat = "Unknown";
    private string _lastAudioError = string.Empty;
    private long _captureRestartCount;
    private long _endpointProbeErrorCount;
    private AudioGainState _gainState = new(false, 1.0f, string.Empty);
    private AudioLevelDelivery _levelDelivery = new(null, 0, []);

    public AudioStreamingService(DeviceDiscoveryService discovery, WindowsAudioService windowsAudio)
    {
        _discovery = discovery;
        _windowsAudio = windowsAudio;
    }

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public long SentPackets => Interlocked.Read(ref _sentPackets);
    public long SentLevelPackets => Interlocked.Read(ref _sentLevelPackets);
    public AudioLevelDelivery LevelDelivery => Volatile.Read(ref _levelDelivery);
    public string Status => _status;
    public string EndpointName => _endpointName;
    public string CaptureFormat => _captureFormat;
    public string LastAudioError => _lastAudioError;
    public long CaptureRestartCount => Interlocked.Read(ref _captureRestartCount);
    public long EndpointProbeErrorCount => Interlocked.Read(ref _endpointProbeErrorCount);
    public Task Completion => _runTask ?? Task.CompletedTask;

    public Task StartAsync()
    {
        _runTask ??= Task.Run(() => RunAsync(_stop.Token));
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _endpointName = device.FriendlyName;
                await CaptureDeviceAsync(device, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastAudioError = ex.ToString();
                Interlocked.Increment(ref _captureRestartCount);
                _status = $"Audio error: {ex.Message}";
                _endpointName = "No playback device";
                try
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task CaptureDeviceAsync(MMDevice device, CancellationToken cancellationToken)
    {
        string deviceId = device.ID;
        // Dispose capture (which joins its callback thread) before the signal.
        using var signal = new SemaphoreSlim(0, 1);
        using var capture = new WasapiLoopbackCapture(device);
        _captureFormat = capture.WaveFormat.ToString();
        var provider = new BufferedWaveProvider(capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        capture.DataAvailable += (_, args) =>
        {
            provider.AddSamples(args.Buffer, 0, args.BytesRecorded);
            if (signal.CurrentCount == 0)
            {
                signal.Release();
            }
        };
        capture.RecordingStopped += (_, args) =>
        {
            stopped.TrySetResult(args.Exception);
        };

        var source = new WaveFormatOverrideProvider(
            provider,
            capture.WaveFormat.AsStandardWaveFormat()).ToSampleProvider();
        ISampleProvider mono = new MonoSampleProvider(source);
        if (mono.WaveFormat.SampleRate != AudioProtocol.SampleRate)
        {
            mono = new WdlResamplingSampleProvider(mono, AudioProtocol.SampleRate);
        }

        using var deviceStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var processTask = ProcessAudioAsync(mono, signal, deviceStop.Token);
        var gainTask = PollGainStateAsync(deviceStop.Token);
        try
        {
            capture.StartRecording();
            _status = "Capturing system audio";
            while (!cancellationToken.IsCancellationRequested && !stopped.Task.IsCompleted)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                if (processTask.IsCompleted) { await processTask.ConfigureAwait(false); break; }
                if (gainTask.IsCompleted) { await gainTask.ConfigureAwait(false); break; }
                try
                {
                    var current = _windowsAudio.GetState();
                    if (!string.Equals(current.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        _status = "Switching playback device";
                        break;
                    }
                }
                catch (AudioDeviceUnavailableException ex)
                {
                    _lastAudioError = ex.ToString();
                    Interlocked.Increment(ref _endpointProbeErrorCount);
                }
            }
        }
        finally
        {
            deviceStop.Cancel();
            capture.StopRecording();
            try
            {
                await Task.WhenAll(processTask, gainTask).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (stopped.Task.IsCompletedSuccessfully && stopped.Task.Result is { } error)
        {
            throw new IOException("Audio capture stopped unexpectedly.", error);
        }
    }

    private async Task ProcessAudioAsync(
        ISampleProvider source,
        SemaphoreSlim signal,
        CancellationToken cancellationToken)
    {
        var sampleBuffer = new float[4096];
        var chunker = new PcmStreamChunker();
        TcpClient? tcpClient = null;
        NetworkStream? audioStream = null;
        string? connectedTargetKey = null;
        long nextWriteAt = 0;
        long nextLevelWriteAt = 0;
        byte pendingPeakLevel = 0;
        var streamInterval = Stopwatch.Frequency
            * AudioProtocol.FramesPerChunk / AudioProtocol.SampleRate;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                for (var iteration = 0; iteration < 32; iteration++)
                {
                    var samplesRead = source.Read(sampleBuffer, 0, sampleBuffer.Length);
                    if (samplesRead <= 0)
                    {
                        break;
                    }

                    var target = _discovery.SelectedDevice;
                    var gainState = Volatile.Read(ref _gainState);
                    pendingPeakLevel = Math.Max(
                        pendingPeakLevel,
                        CalculatePeakLevel(sampleBuffer, 0, samplesRead, gainState));

                    var nowForLevel = Stopwatch.GetTimestamp();
                    if (nextLevelWriteAt == 0 || nowForLevel >= nextLevelWriteAt)
                    {
                        // Every discovered display needs the meter, including devices
                        // that are not selected as the single USB PCM destination.
                        await SendLevelsAsync(_discovery.Devices, pendingPeakLevel, cancellationToken)
                            .ConfigureAwait(false);
                        pendingPeakLevel = 0;
                        nextLevelWriteAt = nowForLevel + Stopwatch.Frequency / 10;
                    }

                    var canSendPcm = !_paused && target is { UsbMounted: true };
                    var targetKey = canSendPcm
                        ? $"{target!.DeviceId}|{target.Address}|{target.AudioPort}"
                        : null;

                    if (!canSendPcm)
                    {
                        audioStream?.Dispose();
                        tcpClient?.Dispose();
                        audioStream = null;
                        tcpClient = null;
                        connectedTargetKey = null;
                        nextWriteAt = 0;
                        chunker.Reset();
                        _status = _paused
                            ? "Paused"
                            : target is null
                                ? "Waiting for T-Display"
                                : "Sending level only";
                        continue;
                    }

                    if (audioStream is null
                        || !string.Equals(connectedTargetKey, targetKey, StringComparison.Ordinal))
                    {
                        audioStream?.Dispose();
                        tcpClient?.Dispose();
                        audioStream = null;
                        tcpClient = null;
                        connectedTargetKey = null;
                        nextWriteAt = 0;
                        chunker.Reset();

                        try
                        {
                            tcpClient = new TcpClient(AddressFamily.InterNetwork)
                            {
                                NoDelay = true,
                                SendBufferSize = AudioProtocol.ChunkBytes * 8,
                            };
                            await tcpClient.ConnectAsync(
                                target!.Address,
                                target.AudioPort,
                                cancellationToken).ConfigureAwait(false);
                            audioStream = tcpClient.GetStream();
                            connectedTargetKey = targetKey;
                            nextWriteAt = Stopwatch.GetTimestamp();
                        }
                        catch (Exception ex) when (ex is SocketException or IOException)
                        {
                            audioStream?.Dispose();
                            tcpClient?.Dispose();
                            audioStream = null;
                            tcpClient = null;
                            _status = "Reconnecting audio stream";
                            continue;
                        }
                    }

                    foreach (var chunk in chunker.AddSamples(sampleBuffer, 0, samplesRead, gainState))
                    {
                        try
                        {
                            var now = Stopwatch.GetTimestamp();
                            if (nextWriteAt == 0 || now - nextWriteAt > streamInterval * 4)
                            {
                                nextWriteAt = now;
                            }
                            await DelayUntilAsync(nextWriteAt, cancellationToken).ConfigureAwait(false);
                            await audioStream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                            nextWriteAt += streamInterval;
                            Interlocked.Increment(ref _sentPackets);
                        }
                        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
                        {
                            audioStream?.Dispose();
                            tcpClient?.Dispose();
                            audioStream = null;
                            tcpClient = null;
                            connectedTargetKey = null;
                            nextWriteAt = 0;
                            chunker.Reset();
                            _status = "Reconnecting audio stream";
                            break;
                        }
                    }
                    if (audioStream is not null)
                    {
                        _status = gainState.Muted
                            ? "Streaming silence (muted)"
                            : "Streaming PCM to USB";
                    }
                }
            }
        }
        finally
        {
            audioStream?.Dispose();
            tcpClient?.Dispose();
        }
    }

    internal async Task SendLevelsAsync(IReadOnlyList<BridgeDevice> devices, byte peakLevel, CancellationToken ct)
    {
        var delivered = new List<string>();
        if (!_paused)
        {
            var packet = AudioProtocol.CreateLevelPacket(peakLevel);
            foreach (var device in devices)
            {
                var endpoint = new IPEndPoint(device.Address, device.LevelPort);
                try
                {
                    await _levelUdp.SendAsync(packet.AsMemory(), endpoint, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _sentLevelPackets);
                    delivered.Add(endpoint.ToString());
                }
                catch (SocketException) when (!ct.IsCancellationRequested)
                {
                    // One unreachable display must not suppress the others.
                }
            }
        }
        Volatile.Write(ref _levelDelivery, new(DateTimeOffset.UtcNow, peakLevel, delivered.ToArray()));
    }

    private static byte CalculatePeakLevel(
        float[] samples,
        int offset,
        int count,
        AudioGainState gainState)
    {
        if (gainState.Muted || count <= 0)
        {
            return 0;
        }

        var peak = 0.0f;
        for (var index = offset; index < offset + count; index++)
        {
            peak = Math.Max(peak, Math.Abs(samples[index] * gainState.LinearGain));
        }
        return (byte)Math.Clamp((int)MathF.Round(peak * 100.0f), 0, 100);
    }

    private async Task PollGainStateAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var gainState = _windowsAudio.GetGainState();
                Volatile.Write(ref _gainState, gainState);
            }
            catch (AudioDeviceUnavailableException)
            {
                // Keep the last known gain during transient endpoint changes or volume requests.
            }

            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task DelayUntilAsync(long targetTimestamp, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = targetTimestamp - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            var remainingMilliseconds = remaining * 1000.0 / Stopwatch.Frequency;
            if (remainingMilliseconds > 2.0)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(remainingMilliseconds - 1.0),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                Thread.SpinWait(64);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _levelUdp.Dispose();
        _stop.Dispose();
    }
}

internal sealed class WaveFormatOverrideProvider : IWaveProvider
{
    private readonly IWaveProvider _source;

    public WaveFormatOverrideProvider(IWaveProvider source, WaveFormat waveFormat)
    {
        _source = source;
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        return _source.Read(buffer, offset, count);
    }
}
