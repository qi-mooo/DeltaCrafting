using System.Xml.Linq;
using DeltaCrafter.Core.L1;
using Xunit;

namespace DeltaCrafter.Core.Tests;

public class AutostartTaskDefinitionTests
{
    private const string Exe = @"C:\Users\i\Desktop\助手 & 工具\DeltaCrafter.exe";
    private const string Sid = "S-1-5-21-123-456-789-1001";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void BuildsInteractiveDelayedStartupWithRecoveryAndEscapedPath()
    {
        var task = XElement.Parse(AutostartTaskDefinition.Build(Exe, Sid));
        string Value(string name) => task.Descendants(Ns + name).First().Value;
        Assert.Equal(Exe, Value("Command"));
        Assert.Equal(Path.GetDirectoryName(Exe), Value("WorkingDirectory"));
        Assert.Equal("--minimized", Value("Arguments"));
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal("PT30S", Value("Delay"));
        Assert.All(task.Descendants(Ns + "UserId"), e => Assert.Equal(Sid, e.Value));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value("StopIfGoingOnBatteries"));
        Assert.Equal("PT0S", Value("ExecutionTimeLimit"));
        Assert.Equal("PT1M", Value("Interval"));
        Assert.Equal("3", Value("Count"));
    }

    [Fact]
    public void LegacySchtasksDefaultsNeedMigration()
    {
        var task = XElement.Parse(AutostartTaskDefinition.Build(Exe, Sid));
        task.Descendants(Ns + "Delay").Remove();
        task.Descendants(Ns + "RestartOnFailure").Remove();
        task.Descendants(Ns + "ExecutionTimeLimit").Single().Value = "PT72H";
        Assert.True(AutostartTaskDefinition.IsEnabled(task.ToString()));
        Assert.False(AutostartTaskDefinition.IsCurrent(task.ToString(), Exe, Sid));
    }

    [Theory]
    [InlineData("Settings")]
    [InlineData("LogonTrigger")]
    public void DisabledTaskOrTriggerIsNotReportedEnabled(string parent)
    {
        var task = XElement.Parse(AutostartTaskDefinition.Build(Exe, Sid));
        task.Descendants(Ns + parent).Single().Element(Ns + "Enabled")!.Value = "false";
        Assert.False(AutostartTaskDefinition.IsEnabled(task.ToString()));
    }

    [Fact]
    public void UnchangedTaskNeedsNoReregistrationButNewPathOrUserDoes()
    {
        var task = XElement.Parse(AutostartTaskDefinition.Build(Exe, Sid));
        task.AddFirst(new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Date", "2026-10-06")));
        task.Element(Ns + "Settings")!.Add(new XElement(Ns + "Priority", "7"));
        string xml = task.ToString();
        Assert.True(AutostartTaskDefinition.IsCurrent(xml, Exe, Sid));
        Assert.False(AutostartTaskDefinition.IsCurrent(xml, Exe.Replace("Desktop", "Downloads"), Sid));
        Assert.False(AutostartTaskDefinition.IsCurrent(xml, Exe, Sid + "0"));
    }

    [Fact]
    public void WindowsExportWithOmittedDefaultsAndResolvedUserDoesNotMigrateAgain()
    {
        var task = XElement.Parse(AutostartTaskDefinition.Build(Exe, Sid));
        foreach (string name in new[] { "Enabled", "AllowStartOnDemand", "RunOnlyIfNetworkAvailable", "RunOnlyIfIdle" })
            task.Descendants(Ns + name).Remove();
        task.Descendants(Ns + "LogonTrigger").Single().Element(Ns + "UserId")!.Value = @"PC-06\i";
        Assert.True(AutostartTaskDefinition.IsEnabled(task.ToString()));
        Assert.True(AutostartTaskDefinition.IsCurrent(task.ToString(), Exe, Sid, @"PC-06\i"));
        Assert.False(AutostartTaskDefinition.IsCurrent(task.ToString(), Exe, Sid, @"PC-06\other"));
    }
}
