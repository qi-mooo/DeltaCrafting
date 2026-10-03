using System.Globalization;
using Microsoft.Win32;

namespace DeltaCrafter.Core.L1;

public sealed record SteamGameInstallation(string SteamExe, string AppId, string InstallDirectory);

/// <summary>只读取本机 Steam 库与安装清单;远程串流库不作为本机安装。</summary>
public static class SteamInstallBrick
{
    public static SteamGameInstallation Resolve(string steamPath, string appId)
    {
        appId = NormalizeAppId(appId);
        if (string.IsNullOrWhiteSpace(steamPath))
        {
            steamPath = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string
                ?? "";
            if (string.IsNullOrWhiteSpace(steamPath))
            {
                var root = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                if (!string.IsNullOrWhiteSpace(root)) steamPath = Path.Combine(root, "steam.exe");
            }
        }
        if (!File.Exists(steamPath) || !string.Equals(Path.GetFileName(steamPath), "steam.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("未找到本机 steam.exe,请安装 Steam 或在「设置 → Steam 客户端」中选择 steam.exe。");

        steamPath = Path.GetFullPath(steamPath);
        var rootDirectory = Path.GetDirectoryName(steamPath)!;
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootDirectory };
        string libraryFile = Path.Combine(rootDirectory, "steamapps", "libraryfolders.vdf");
        if (File.Exists(libraryFile))
        {
            var folders = ReadVdf(libraryFile).Object("libraryfolders");
            if (folders is not null)
                foreach (var (key, value) in folders.Values)
                {
                    if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out _)) continue;
                    // 兼容旧版 "1" "D:\\SteamLibrary" 和新版带 path/apps 的节点。
                    string? path = value is VdfNode node ? node.String("path") : value as string;
                    if (!string.IsNullOrWhiteSpace(path)) libraries.Add(path);
                }
        }

        foreach (string library in libraries)
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            var state = ReadVdf(manifest).Object("AppState");
            if (state?.String("appid") != appId) continue;
            if (!uint.TryParse(state.String("StateFlags"), NumberStyles.None, CultureInfo.InvariantCulture, out uint flags)
                || (flags & 4) == 0) continue; // FullyInstalled;仅下载中/卸载后的残留不算安装完成。
            string? folder = state.String("installdir");
            if (string.IsNullOrWhiteSpace(folder) || Path.IsPathRooted(folder)) continue;
            string common = Path.GetFullPath(Path.Combine(library, "steamapps", "common"));
            string installed = Path.GetFullPath(Path.Combine(common, folder));
            if (!IsInsideDirectory(installed, common) || !Directory.Exists(installed)) continue;
            if (!Directory.EnumerateFiles(installed, "*.exe", SearchOption.AllDirectories).Any()) continue;
            return new SteamGameInstallation(steamPath, appId, installed);
        }
        throw new InvalidOperationException($"未找到 Steam App {appId} 在本机完整安装的游戏文件。请在此电脑的 Steam 中完成安装/更新;仅可串流的游戏不能自动执行。");
    }

    internal static string NormalizeAppId(string appId)
    {
        if (!uint.TryParse(appId?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint id) || id == 0)
            throw new InvalidOperationException("Steam App ID 必须是正整数,请在「设置」中填写游戏商店页面的 App ID。");
        return id.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool IsInsideDirectory(string path, string directory) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static VdfNode ReadVdf(string path)
    {
        try { return VdfNode.Parse(File.ReadAllText(path)); }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Steam 安装信息损坏:{path}。请在 Steam 中修复游戏安装。", ex);
        }
    }

    /// <summary>Valve KeyValues 文本:按层级读取,避免把 apps 节点或注释中的路径误当成库。</summary>
    private sealed class VdfNode
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? String(string key) => Values.GetValueOrDefault(key) as string;
        public VdfNode? Object(string key) => Values.GetValueOrDefault(key) as VdfNode;

        public static VdfNode Parse(string text)
        {
            int position = 0;
            return ReadNode(nested: false, depth: 0);

            void SkipSpace()
            {
                while (position < text.Length)
                {
                    if (char.IsWhiteSpace(text[position]) || text[position] == '\uFEFF') { position++; continue; }
                    if (text[position] == '/' && position + 1 < text.Length && text[position + 1] == '/')
                    {
                        while (position < text.Length && text[position] != '\n') position++;
                        continue;
                    }
                    break;
                }
            }

            string ReadString()
            {
                SkipSpace();
                if (position >= text.Length || text[position++] != '"') throw new InvalidDataException("预期带引号的 KeyValues 字符串。");
                var result = new System.Text.StringBuilder();
                while (position < text.Length)
                {
                    char c = text[position++];
                    if (c == '"') return result.ToString();
                    if (c == '\\' && position < text.Length && text[position] is '\\' or '"') c = text[position++];
                    result.Append(c);
                }
                throw new InvalidDataException("KeyValues 字符串未闭合。");
            }

            VdfNode ReadNode(bool nested, int depth)
            {
                if (depth > 32) throw new InvalidDataException("KeyValues 嵌套过深。");
                var node = new VdfNode();
                while (true)
                {
                    SkipSpace();
                    if (position == text.Length)
                    {
                        if (nested) throw new InvalidDataException("KeyValues 节点未闭合。");
                        return node;
                    }
                    if (text[position] == '}')
                    {
                        if (!nested) throw new InvalidDataException("多余的 KeyValues 结束括号。");
                        position++;
                        return node;
                    }
                    string key = ReadString();
                    SkipSpace();
                    if (position < text.Length && text[position] == '{')
                    {
                        position++;
                        node.Values[key] = ReadNode(nested: true, depth + 1);
                    }
                    else node.Values[key] = ReadString();
                }
            }
        }
    }
}
