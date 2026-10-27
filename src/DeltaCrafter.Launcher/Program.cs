using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DeltaCrafter.Launcher;

/// <summary>
/// 自安装启动器:把内嵌的完整客户端解压到 %LocalAppData%\DeltaCrafter\app\&lt;PayloadId&gt;
/// 后启动它。PayloadId 含版本与内容哈希,同一版本只解压一次;解压先落到临时目录再整体改名,
/// 目标目录里有 DeltaCrafter.exe 即代表完整。客户端经环境变量得知启动器路径,
/// 开机自启与随包固件都以这个稳定路径为准,不随版本目录变化。
/// </summary>
internal static class Program
{
    /// <summary>与 DeltaCrafter.App.Services.AppLocation 约定的变量名。</summary>
    private const string LauncherPathVariable = "DELTACRAFTER_LAUNCHER";
    private const string AppExeName = "DeltaCrafter.exe";

    [STAThread]
    private static int Main(string[] args)
    {
        // 安装程序用 --prepare 预先解压,避免安装后首次启动等待。
        bool prepareOnly = args is ["--prepare"];
        try
        {
            string appExe = EnsureExtracted();
            if (prepareOnly) return 0;

            var psi = new ProcessStartInfo(appExe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(appExe)!,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);
            psi.Environment[LauncherPathVariable] = Environment.ProcessPath!;
            Process.Start(psi)?.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            if (!prepareOnly)
                MessageBoxW(IntPtr.Zero, $"无法准备或启动三角洲特勤助手:\n\n{ex.Message}",
                    "三角洲特勤助手", MbIconError);
            return 1;
        }
    }

    private static string EnsureExtracted()
    {
        string payloadId = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PayloadId")?.Value
            ?? string.Empty;
        if (payloadId.Length == 0)
            throw new InvalidOperationException("启动器未内嵌客户端,请使用 scripts\\build-release.ps1 构建。");

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeltaCrafter", "app");
        string target = Path.Combine(root, payloadId);
        string appExe = Path.Combine(target, AppExeName);
        if (File.Exists(appExe)) return appExe;

        // 安装程序预解压与用户双击可能并发,串行化后再复查。
        using var mutex = new Mutex(false, @"Local\DeltaCrafter-Launcher-Extract");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { }
        try
        {
            if (File.Exists(appExe)) return appExe;

            Directory.CreateDirectory(root);
            string staging = Path.Combine(root, ".staging-" + Guid.NewGuid().ToString("N"));
            using (var payload = typeof(Program).Assembly.GetManifestResourceStream("payload.zip")
                ?? throw new InvalidOperationException("启动器内嵌客户端缺失。"))
            {
                ZipFile.ExtractToDirectory(payload, staging);
            }
            if (!File.Exists(Path.Combine(staging, AppExeName)))
                throw new InvalidOperationException($"内嵌客户端缺少 {AppExeName}。");

            // 缺 exe 的残缺目标目录(例如被手动删过文件)整体替换。
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            RemoveStale(root, payloadId);
            return appExe;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>
    /// 清理旧版本与中断留下的临时目录。先改名再删:仍在运行的旧版本持有文件句柄,
    /// 改名会失败而原样保留,不会被删成半截。
    /// </summary>
    private static void RemoveStale(string root, string keep)
    {
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                string trash = Path.Combine(root, ".trash-" + Guid.NewGuid().ToString("N"));
                Directory.Move(dir, trash);
                Directory.Delete(trash, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private const uint MbIconError = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
