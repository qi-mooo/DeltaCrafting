using System.Globalization;
using DeltaCrafter.Core.L0;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public sealed class SteamLaunchTests : IDisposable
{
    private const string AppId = "2507950";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "delta-steam-tests-" + Guid.NewGuid());
    private string SteamRoot => Path.Combine(_root, "Steam 客户端");
    private string SteamExe => Path.Combine(SteamRoot, "steam.exe");

    public SteamLaunchTests()
    {
        Directory.CreateDirectory(Path.Combine(SteamRoot, "steamapps"));
        File.WriteAllText(SteamExe, "test fixture; never executed");
    }

    [Fact]
    public void Resolves_local_install_without_requiring_library_index()
    {
        var installed = Install(SteamRoot);
        var result = SteamInstallBrick.Resolve(SteamExe, AppId);
        Assert.Equal(installed, result.InstallDirectory);
        Assert.Equal(SteamExe, result.SteamExe);
        Assert.Equal(AppId, result.AppId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finds_secondary_library_in_modern_and_legacy_formats(bool legacy)
    {
        string library = Path.Combine(_root, "第二个 Steam 库");
        string installed = Install(library);
        string entry = legacy ? $"\"1\" \"{Escape(library)}\""
            : $"\"1\" {{ \"path\" \"{Escape(library)}\" \"apps\" {{ \"{AppId}\" \"100\" }} }}";
        File.WriteAllText(Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf"),
            $"// paths may contain spaces and Unicode\n\"libraryfolders\" {{ {entry} }}");
        Assert.Equal(installed, SteamInstallBrick.Resolve(SteamExe, AppId).InstallDirectory);
    }

    [Fact]
    public void Library_app_entry_without_local_manifest_is_not_an_installation()
    {
        File.WriteAllText(Path.Combine(SteamRoot, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"0\" {{ \"path\" \"{Escape(SteamRoot)}\" \"apps\" {{ \"{AppId}\" \"100\" }} }} }}");
        var ex = Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(SteamExe, AppId));
        Assert.Contains("仅可串流", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(1024)]
    public void Rejects_incomplete_or_removed_install(uint stateFlags)
    {
        Install(SteamRoot, flags: stateFlags);
        Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(SteamExe, AppId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Stale_manifest_without_game_files_is_rejected(bool removeDirectory)
    {
        string installed = Install(SteamRoot);
        if (removeDirectory) Directory.Delete(installed, recursive: true);
        else File.Delete(Path.Combine(installed, "Game", "DeltaForce.exe"));
        Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(SteamExe, AppId));
    }

    [Fact]
    public void Manifest_app_id_must_match_the_requested_game()
    {
        Install(SteamRoot, manifestId: "123");
        Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(SteamExe, AppId));
    }

    [Fact]
    public void Install_directory_cannot_escape_steam_common_directory()
    {
        Install(SteamRoot, folder: "../outside");
        Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(SteamExe, AppId));
    }

    [Fact]
    public void Malformed_manifest_reports_its_path()
    {
        string manifest = Path.Combine(SteamRoot, "steamapps", $"appmanifest_{AppId}.acf");
        File.WriteAllText(manifest, "\"AppState\" { \"appid\" \"2507950\"");
        var ex = Assert.Throws<InvalidDataException>(() => SteamInstallBrick.Resolve(SteamExe, AppId));
        Assert.Contains(manifest, ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2507950 /stream")]
    [InlineData("steam://rungameid/2507950")]
    [InlineData("4294967296")]
    [InlineData("２５０７９５０")]
    public void Invalid_app_id_is_rejected_before_launch(string value)
    {
        Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(SteamExe, value));
        Assert.Throws<InvalidOperationException>(() => GameProcessBrick.CreateSteamStartInfo(
            new SteamGameInstallation(SteamExe, value, _root)));
    }

    [Fact]
    public void Launch_uses_selected_local_steam_and_separate_app_id_argument()
    {
        var info = GameProcessBrick.CreateSteamStartInfo(new SteamGameInstallation(SteamExe, " 2507950 ", _root));
        Assert.Equal(SteamExe, info.FileName);
        Assert.Equal(SteamRoot, info.WorkingDirectory);
        Assert.Equal(new[] { "-applaunch", AppId }, info.ArgumentList);
        Assert.True(info.UseShellExecute);
    }

    [Fact]
    public void Steam_client_path_must_be_an_existing_steam_executable()
    {
        string other = Path.Combine(SteamRoot, "other.exe");
        File.WriteAllText(other, "test");
        Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(other, AppId));
        File.Delete(SteamExe);
        Assert.Throws<InvalidOperationException>(() => SteamInstallBrick.Resolve(SteamExe, AppId));
    }

    [Fact]
    public void Stream_client_and_neighboring_install_cannot_pass_process_filter()
    {
        string game = Path.Combine(_root, "Delta Force");
        Assert.True(GameWindowBrick.IsLocalGameExecutable(Path.Combine(game, "Game", "DeltaForce.exe"), game));
        Assert.False(GameWindowBrick.IsLocalGameExecutable(Path.Combine(SteamRoot, "streaming_client.exe"), game));
        Assert.False(GameWindowBrick.IsLocalGameExecutable(Path.Combine(game, "streaming_client.exe"), game));
        Assert.False(GameWindowBrick.IsLocalGameExecutable(Path.Combine(game, "steam.exe"), game));
        Assert.False(GameWindowBrick.IsLocalGameExecutable(Path.Combine(game + " other", "DeltaForce.exe"), game));
        Assert.False(GameWindowBrick.IsLocalGameExecutable(Path.Combine(game, "..", "other.exe"), game));
    }

    [Fact]
    public void Steam_english_title_fallback_preserves_explicit_title_and_class_rules()
    {
        var window = new GameWindowInfo(1, "Delta Force", "GameClass", 123);
        var rule = new WindowMatchRule();
        Assert.False(GameWindowBrick.MatchesRule(window, rule));
        Assert.True(GameWindowBrick.MatchesRule(window, rule, steam: true));
        rule.ExactTitle = "三角洲行动";
        Assert.False(GameWindowBrick.MatchesRule(window, rule, steam: true));
        rule.ExactTitle = null;
        rule.ClassName = "OtherClass";
        Assert.False(GameWindowBrick.MatchesRule(window, rule, steam: true));
        rule.ClassName = null;
        rule.TitleContains = "custom game";
        Assert.False(GameWindowBrick.MatchesRule(window, rule, steam: true));
    }

    private string Install(string library, uint flags = 4, string manifestId = AppId, string folder = "Delta Force")
    {
        string steamApps = Path.Combine(library, "steamapps");
        string installed = Path.GetFullPath(Path.Combine(steamApps, "common", folder));
        Directory.CreateDirectory(Path.Combine(installed, "Game"));
        File.WriteAllText(Path.Combine(installed, "Game", "DeltaForce.exe"), "test fixture; never executed");
        File.WriteAllText(Path.Combine(steamApps, $"appmanifest_{AppId}.acf"),
            $"\"AppState\" {{ \"appid\" \"{manifestId}\" \"StateFlags\" \"{flags.ToString(CultureInfo.InvariantCulture)}\" \"installdir\" \"{Escape(folder)}\" }}");
        return installed;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
