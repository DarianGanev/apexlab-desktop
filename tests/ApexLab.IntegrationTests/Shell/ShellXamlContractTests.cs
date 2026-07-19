using System.Xml.Linq;

namespace ApexLab.IntegrationTests.Shell;

[TestClass]
public sealed class ShellXamlContractTests
{
    [TestMethod]
    public void Window_declares_a_readable_resizable_rounded_layout()
    {
        var document = LoadXaml("MainWindow.xaml");
        var window = document.Root!;

        Assert.AreEqual("ApexLab — Telemetry Coaching Workbench", (string?)window.Attribute("Title"));
        Assert.AreEqual("1100", (string?)window.Attribute("MinWidth"));
        Assert.AreEqual("700", (string?)window.Attribute("MinHeight"));
        Assert.AreEqual("True", (string?)window.Attribute("UseLayoutRounding"));
        Assert.AreEqual("True", (string?)window.Attribute("SnapsToDevicePixels"));
    }

    [TestMethod]
    public void Primary_navigation_has_exactly_four_keyboard_accessible_buttons()
    {
        var document = LoadXaml("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var primaryButtons = document
            .Descendants(presentation + "Button")
            .Where(element => (string?)element.Attribute("Tag") == "PrimaryNavigation")
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "_Drive", "_Review", "_Coach", "Data & _Settings" },
            primaryButtons.Select(element => (string?)element.Attribute("Content")).ToArray());
        Assert.IsTrue(primaryButtons.All(element => element.Attribute("Command") is not null));
        Assert.IsTrue(primaryButtons.All(element => element.Attribute("CommandParameter") is not null));
    }

    [TestMethod]
    public void Shell_has_visible_focus_text_status_and_honest_foundation_copy()
    {
        var xaml = File.ReadAllText(GetAppFile("MainWindow.xaml"));

        StringAssert.Contains(xaml, "FocusVisualStyle");
        StringAssert.Contains(xaml, "KeyboardNavigation.TabNavigation");
        StringAssert.Contains(xaml, "FOUNDATION • OFFLINE");
        StringAssert.Contains(xaml, "Milestone v0.1");
        StringAssert.Contains(xaml, "No telemetry is being captured in this foundation build.");
        Assert.IsFalse(xaml.Contains("Start Capture", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(xaml.Contains("Analyze", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Advanced_routes_are_secondary_and_named_for_assistive_technology()
    {
        var xaml = File.ReadAllText(GetAppFile("MainWindow.xaml"));

        StringAssert.Contains(xaml, "OpenCornerEditorCommand");
        StringAssert.Contains(xaml, "OpenReplayDiagnosticsCommand");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"Open Corner Editor within Review\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"Open Replay and Diagnostics within Data and Settings\"");
    }

    [TestMethod]
    public void Application_declares_per_monitor_v2_dpi_awareness()
    {
        var project = File.ReadAllText(GetAppFile("ApexLab.App.csproj"));
        var manifest = File.ReadAllText(GetAppFile("app.manifest"));

        StringAssert.Contains(project, "<ApplicationManifest>app.manifest</ApplicationManifest>");
        StringAssert.Contains(manifest, "PerMonitorV2");
    }

    private static XDocument LoadXaml(string fileName) => XDocument.Load(GetAppFile(fileName));

    private static string GetAppFile(string fileName) => Path.Combine(
        FindRepositoryRoot(),
        "src",
        "ApexLab.App",
        fileName);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ApexLab.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the ApexLab repository root.");
    }
}
