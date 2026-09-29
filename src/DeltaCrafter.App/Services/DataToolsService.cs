using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Windows.ApplicationModel.DataTransfer;
using OpenCvSharp;

namespace DeltaCrafter.App.Services;

public sealed class DataToolsService
{
    private readonly DataToolsClient _client = new();
    private readonly DataToolsCache _cache;
    private static readonly HttpClient ImageHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 2 * 1024 * 1024 };
    private readonly Dictionary<string, string> _imageUrls = new();
    private readonly Dictionary<string, DataToolImage> _images = new();
    private readonly Dictionary<string, byte[]> _imageBytes = new();
    private readonly SemaphoreSlim _imageGate = new(1, 1);
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    private readonly HashSet<string> _codes = new(StringComparer.Ordinal);
    private readonly object _codesLock = new();

    public DataToolsService() => _cache = new(_client.FetchAsync,
        Path.Combine(new AppDataBrick().Root, "tools-market.json"));

    public async Task<DataToolResult> FetchAsync(DataToolQuery query, string token, CancellationToken ct)
    {
        var result = await _cache.FetchAsync(query, token, ct);
        lock (_codesLock)
        {
            if (_codes.Count > 5000) _codes.Clear();
            foreach (var entry in result.Entries.Where(e => e.CopyText.Length > 0)) _codes.Add(entry.CopyText);
            if (query.Tool == "market")
            {
                _imageUrls.Clear();
                foreach (var entry in result.Entries.Where(e => e.ImageUrl.Length > 0))
                    _imageUrls[entry.Id] = DataToolsClient.SafeImageUrl(entry.ImageUrl);
            }
        }
        return result;
    }

    private string ImageUrl(string id)
    {
        string? url;
        lock (_codesLock) _imageUrls.TryGetValue(id, out url);
        if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("此物品暂无图片，请重新打开集市。");
        return url;
    }

    public async Task<byte[]> ImageBytesAsync(string id, CancellationToken ct)
    {
        string url = ImageUrl(id);
        await _downloadGate.WaitAsync(ct);
        try
        {
            if (_imageBytes.TryGetValue(url, out var cached)) return cached;
            byte[] bytes = await ImageHttp.GetByteArrayAsync(url, ct);
            if (_imageBytes.Count >= 20) _imageBytes.Clear();
            _imageBytes[url] = bytes;
            return bytes;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("图片加载失败，请重试。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("图片加载超时，请重试。"); }
        finally { _downloadGate.Release(); }
    }

    public async Task<DataToolImage> ImageAsync(string id, CancellationToken ct)
    {
        string url = ImageUrl(id);
        await _imageGate.WaitAsync(ct);
        try
        {
            if (_images.TryGetValue(url, out var cached)) return cached;
            byte[] bytes = await ImageBytesAsync(id, ct);
            using var source = Cv2.ImDecode(bytes, ImreadModes.Unchanged);
            if (source.Empty() || source.Width > 4096 || source.Height > 4096)
                throw new InvalidOperationException("物品图片格式无效。");
            const int size = 96;
            double scale = Math.Min((double)size / source.Width, (double)size / source.Height);
            using var resized = new Mat();
            Cv2.Resize(source, resized, new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale))));
            int channels = resized.Channels();
            if (channels is not (3 or 4)) throw new InvalidOperationException("物品图片格式无效。");
            byte[] pixels = new byte[size * size * 2];
            for (int y = 0; y < resized.Height; y++)
            for (int x = 0; x < resized.Width; x++)
            {
                var c = channels == 4 ? resized.At<Vec4b>(y, x) : default;
                var rgb = channels == 3 ? resized.At<Vec3b>(y, x) : new Vec3b(c.Item0, c.Item1, c.Item2);
                int alpha = channels == 4 ? c.Item3 : 255;
                int packed = ((rgb.Item2 * alpha / 255 >> 3) << 11) | ((rgb.Item1 * alpha / 255 >> 2) << 5) | (rgb.Item0 * alpha / 255 >> 3);
                int offset = ((y + (size - resized.Height) / 2) * size + x + (size - resized.Width) / 2) * 2;
                pixels[offset] = (byte)packed; pixels[offset + 1] = (byte)(packed >> 8);
            }
            var result = new DataToolImage(size, size, Convert.ToBase64String(pixels));
            if (_images.Count >= 20) _images.Clear();
            _images[url] = result;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or OpenCVException)
        { throw new InvalidOperationException("图片加载失败，请重试。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("图片加载超时，请重试。"); }
        finally { _imageGate.Release(); }
    }

    /// <summary>在桌面 UI 线程调用；仅复制本次客户端已查询过的完整改枪码。</summary>
    public DeviceActionResult CopyGunCode(string code)
    {
        lock (_codesLock)
            if (!_codes.Contains(code)) return new(400, "请重新打开改枪码后复制");
        try
        {
            var data = new DataPackage();
            data.SetText(code);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            return new(200, "已复制到 Windows 剪贴板");
        }
        catch (Exception)
        { return new(503, "剪贴板暂不可用，请重试"); }
    }
}
