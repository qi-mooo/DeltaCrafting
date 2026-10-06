using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Serilog;

namespace DeltaCrafter.Core.L1;

/// <summary>
/// 开机自启,基于计划任务(schtasks /RL HIGHEST):本程序需管理员权限,
/// 注册表 Run 键会在开机时弹 UAC,计划任务方案可静默以最高权限启动。
/// 所有 schtasks 失败都带退出码与原始输出抛出,不吞。
/// </summary>
public sealed class AutostartBrick
{
    private const string TaskName = "DeltaCrafter-AutoStart";
    private readonly ILogger _log;

    static AutostartBrick() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public AutostartBrick(ILogger log) => _log = log.ForContext<AutostartBrick>();

    public bool IsEnabled() => ReadTaskXml() is { } xml && AutostartTaskDefinition.IsEnabled(xml);

    public void EnsureCurrent(string exePath)
    {
        string? xml = ReadTaskXml();
        if (xml is null || !AutostartTaskDefinition.IsEnabled(xml)) return;
        using var identity = WindowsIdentity.GetCurrent();
        if (!AutostartTaskDefinition.IsCurrent(xml, exePath, CurrentUserSid(), identity.Name)) Enable(exePath);
    }

    private string? ReadTaskXml()
    {
        var r = Run("/Query", "/TN", TaskName, "/XML");
        return r.ExitCode == 0 ? r.Output : null;
    }

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("无法获取自启任务用户。");
    }

    public void Enable(string exePath)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"DeltaCrafter-autostart-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, AutostartTaskDefinition.Build(exePath, CurrentUserSid()), Encoding.Unicode);
            var r = Run("/Create", "/F", "/TN", TaskName, "/XML", xmlPath);
            if (r.ExitCode != 0)
                throw new InvalidOperationException($"创建开机自启任务失败(退出码 {r.ExitCode}):{r.Output}");
            _log.Information("已配置当前用户登录后延迟 30 秒自启，失败后重试，不限制供电和运行时长。");
        }
        finally { File.Delete(xmlPath); }
    }

    public void Disable()
    {
        var r = Run("/Delete", "/F", "/TN", TaskName);
        if (r.ExitCode != 0 && ReadTaskXml() is not null)
            throw new InvalidOperationException($"删除开机自启任务失败(退出码 {r.ExitCode}):{r.Output}");
        _log.Information("已移除开机自启计划任务。");
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // schtasks 输出走 OEM 简体中文代码页;编码不对只影响报错文本可读性,不影响判定。
            StandardOutputEncoding = Encoding.GetEncoding(936),
            StandardErrorEncoding = Encoding.GetEncoding(936),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 schtasks.exe。");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim());
    }
}
