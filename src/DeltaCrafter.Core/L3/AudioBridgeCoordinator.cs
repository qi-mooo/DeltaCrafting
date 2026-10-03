using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1.AudioBridge;
using Serilog;

namespace DeltaCrafter.Core.L3;

public sealed class AudioBridgeCoordinator(string dataRoot, ILogger log)
{
    private readonly WindowsAudioService _audio = new();
    private AudioBridgeStatus _status = new(null, "正在读取声音状态", "");
    private string _bridgeError = "";
    public AudioBridgeStatus Status => Volatile.Read(ref _status);
    public event Action? Changed;

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(PollAsync(ct), RunBridgeAsync(ct));

    public Task ToggleMuteAsync() => Task.Run(() =>
    {
        try { Publish(_audio.ToggleMute(), ""); }
        catch (AudioDeviceUnavailableException ex) { Publish(null, ex.Message); }
    });

    private void Publish(AudioState? state, string error)
    {
        Volatile.Write(ref _status, new(state, error, Volatile.Read(ref _bridgeError)));
        Changed?.Invoke();
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { Publish(_audio.GetState(), ""); }
                catch (AudioDeviceUnavailableException ex) { Publish(null, ex.Message); }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task RunBridgeAsync(CancellationToken ct)
    {
        string lastError = "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TDisplayAudioBridge", "settings.json");
                var settings = new BridgeSettingsStore(dataRoot, legacy);
                await using var discovery = new DeviceDiscoveryService(settings);
                await using var streaming = new AudioStreamingService(discovery, _audio);
                await using var server = new ControlServer(_audio, () => new
                {
                    status = "ok", service = "t-display-audio-bridge", host = "DeltaCrafter",
                    version = typeof(AudioBridgeCoordinator).Assembly.GetName().Version?.ToString(3),
                    streaming = streaming.Status, sentPackets = streaming.SentPackets,
                    sentLevelPackets = streaming.SentLevelPackets, levelDelivery = streaming.LevelDelivery,
                    endpoint = streaming.EndpointName,
                    captureFormat = streaming.CaptureFormat, captureRestarts = streaming.CaptureRestartCount,
                    endpointProbeErrors = streaming.EndpointProbeErrorCount, lastAudioError = streaming.LastAudioError,
                    devices = discovery.Devices.Select(d => new { d.DeviceId, d.DeviceName, address = d.Address.ToString(), d.UsbMounted }),
                    time = DateTimeOffset.UtcNow,
                });
                // Bind control first: a legacy instance occupying 8765 must not
                // leave a second discovery broadcaster or capture loop running.
                server.Start();
                await discovery.StartAsync().ConfigureAwait(false);
                await streaming.StartAsync().ConfigureAwait(false);
                Volatile.Write(ref _bridgeError, "");
                lastError = "";
                log.Information("内置静音与音频桥已启动: TCP 8765 / UDP 40100。");
                var ended = await Task.WhenAny(server.Completion, discovery.Completion, streaming.Completion,
                    Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    await ended.ConfigureAwait(false);
                    throw new IOException("音频桥后台任务意外结束。");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Volatile.Write(ref _bridgeError, "S3 音频桥连接异常，正在重试");
                if (lastError != ex.Message) log.Warning(ex, "内置音频桥不可用,15 秒后重试。");
                lastError = ex.Message;
            }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
