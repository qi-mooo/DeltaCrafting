using System.Xml.Linq;

namespace DeltaCrafter.Core.L1;

/// <summary>自启任务的声明和旧配置检测；不访问 Windows 计划任务。</summary>
internal static class AutostartTaskDefinition
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string Build(string exePath, string userSid)
    {
        var root = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "LogonTrigger",
                    E("Enabled", "true"), E("UserId", userSid), E("Delay", "PT30S"))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    E("UserId", userSid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                E("MultipleInstancesPolicy", "IgnoreNew"),
                E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"),
                E("StartWhenAvailable", "true"), E("RunOnlyIfNetworkAvailable", "false"),
                E("AllowStartOnDemand", "true"), E("Enabled", "true"),
                E("RunOnlyIfIdle", "false"), E("ExecutionTimeLimit", "PT0S"),
                new XElement(Ns + "RestartOnFailure", E("Interval", "PT1M"), E("Count", "3"))),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec", E("Command", exePath), E("Arguments", "--minimized"),
                    E("WorkingDirectory", Path.GetDirectoryName(exePath) ?? ""))));
        return root.ToString();
    }

    public static bool IsEnabled(string xml)
    {
        var task = XElement.Parse(xml);
        return task.Element(Ns + "Settings")?.Element(Ns + "Enabled")?.Value != "false"
            && task.Element(Ns + "Triggers")?.Elements(Ns + "LogonTrigger")
                .Any(t => t.Element(Ns + "Enabled")?.Value != "false") == true;
    }

    public static bool IsCurrent(string xml, string exePath, string userSid, string? userName = null)
    {
        var actual = XElement.Parse(xml);
        var expected = XElement.Parse(Build(exePath, userSid));
        return actual.Element(Ns + "Triggers")?.Elements().Count() == 1
            && actual.Element(Ns + "Actions")?.Elements().Count() == 1
            && actual.Element(Ns + "Principals")?.Elements().Count() == 1
            && Contains(actual, expected, userName);
    }

    // schtasks 会添加默认项和注册信息；只比较本程序必须保证的设置。
    private static bool Contains(XElement? actual, XElement expected, string? userName)
    {
        // Windows 导出 XML 会省略默认值，并把登录触发器的 SID 改为 DOMAIN\user。
        if (actual is null) return expected.Name.LocalName switch
        {
            "Enabled" or "AllowStartOnDemand" => expected.Value == "true",
            "RunOnlyIfNetworkAvailable" or "RunOnlyIfIdle" => expected.Value == "false",
            _ => false,
        };
        return actual.Name == expected.Name
        && expected.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != "version")
            .All(a => actual.Attribute(a.Name)?.Value == a.Value)
        && (expected.HasElements
            ? expected.Elements().All(e => Contains(actual.Element(e.Name), e, userName))
            : actual.Value == expected.Value || (expected.Name.LocalName == "UserId"
                && string.Equals(actual.Value, userName, StringComparison.OrdinalIgnoreCase)));
    }

    private static XElement E(string name, string value) => new(Ns + name, value);
}
