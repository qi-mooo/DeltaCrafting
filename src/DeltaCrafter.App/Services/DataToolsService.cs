using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Windows.ApplicationModel.DataTransfer;

namespace DeltaCrafter.App.Services;

public sealed class DataToolsService
{
    private readonly DataToolsClient _client = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _codes = new(StringComparer.Ordinal);
    private readonly object _codesLock = new();

    public async Task<DataToolResult> FetchAsync(DataToolQuery query, string token, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException("工具正在查询，请稍后重试。");
        try
        {
            var result = await _client.FetchAsync(query, token, ct);
            lock (_codesLock)
            {
                if (_codes.Count > 5000) _codes.Clear();
                foreach (var entry in result.Entries.Where(e => e.CopyText.Length > 0)) _codes.Add(entry.CopyText);
            }
            return result;
        }
        finally { _gate.Release(); }
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
