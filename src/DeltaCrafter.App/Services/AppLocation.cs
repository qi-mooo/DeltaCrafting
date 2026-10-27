namespace DeltaCrafter.App.Services;

/// <summary>
/// 发布形态下客户端由自安装启动器从 %LocalAppData%\DeltaCrafter\app\&lt;版本&gt; 启动。
/// 对外登记的路径(开机自启)与随包文件(Firmware\)都以稳定的启动器为准,
/// 不能用随版本变化、更新后会被清理的解压目录。直接运行构建输出时回落到自身路径。
/// </summary>
public static class AppLocation
{
    public static string LauncherPath { get; } =
        Environment.GetEnvironmentVariable("DELTACRAFTER_LAUNCHER") is { Length: > 0 } path && File.Exists(path)
            ? path
            : Environment.ProcessPath!;

    public static string LauncherDirectory => Path.GetDirectoryName(LauncherPath)!;
}
