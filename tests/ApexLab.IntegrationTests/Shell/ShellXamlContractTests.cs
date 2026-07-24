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
    public void Primary_navigation_has_exactly_four_keyboard_accessible_selection_controls()
    {
        var document = LoadXaml("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var primaryControls = document
            .Descendants(presentation + "RadioButton")
            .Where(element => (string?)element.Attribute("Tag") == "PrimaryNavigation")
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "_Drive", "_Review", "_Coach", "Data & _Settings" },
            primaryControls.Select(element => (string?)element.Attribute("Content")).ToArray());
        Assert.IsTrue(primaryControls.All(element => element.Attribute("Command") is not null));
        Assert.IsTrue(primaryControls.All(element => element.Attribute("CommandParameter") is not null));
        Assert.IsTrue(primaryControls.All(element => element.Attribute("IsChecked") is not null));
        Assert.IsTrue(primaryControls.All(element => element.Attribute("GroupName")?.Value == "PrimaryWorkflow"));
    }

    [TestMethod]
    public void Shell_has_visible_focus_and_honest_capture_controls()
    {
        var xaml = File.ReadAllText(GetAppFile("MainWindow.xaml"));

        StringAssert.Contains(xaml, "FocusVisualStyle");
        StringAssert.Contains(xaml, "KeyboardNavigation.TabNavigation");
        StringAssert.Contains(xaml, "LOCAL • F1 25");
        StringAssert.Contains(xaml, "Milestone v0.2");
        StringAssert.Contains(xaml, "UDP On");
        StringAssert.Contains(xaml, "127.0.0.1");
        StringAssert.Contains(xaml, "20777");
        StringAssert.Contains(xaml, "Capture.ArmCommand");
        StringAssert.Contains(xaml, "Capture.StopCommand");
        StringAssert.Contains(xaml, "Capture.StatusText");
        Assert.IsFalse(xaml.Contains("Analyze", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Advanced_routes_are_secondary_and_named_for_assistive_technology()
    {
        var document = LoadXaml("MainWindow.xaml");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var review = FindNamedElement(document, presentation + "RadioButton", xamlNamespace, "ReviewNavigation");
        var reviewTools = FindNamedElement(document, presentation + "StackPanel", xamlNamespace, "ReviewToolsPanel");
        var data = FindNamedElement(document, presentation + "RadioButton", xamlNamespace, "DataSettingsNavigation");
        var dataTools = FindNamedElement(document, presentation + "StackPanel", xamlNamespace, "DataToolsPanel");

        Assert.AreSame(review.Parent, reviewTools.Parent);
        Assert.AreSame(data.Parent, dataTools.Parent);
        Assert.AreEqual(review, reviewTools.ElementsBeforeSelf().Last());
        Assert.AreEqual(data, dataTools.ElementsBeforeSelf().Last());
        StringAssert.Contains((string?)reviewTools.Attribute("Visibility") ?? string.Empty, "AreReviewToolsVisible");
        StringAssert.Contains((string?)dataTools.Attribute("Visibility") ?? string.Empty, "AreDataToolsVisible");
        StringAssert.Contains(reviewTools.ToString(), "REVIEW TOOLS");
        StringAssert.Contains(reviewTools.ToString(), "OpenCornerEditorCommand");
        StringAssert.Contains(reviewTools.ToString(), "Open Corner Editor within Review");
        StringAssert.Contains(dataTools.ToString(), "DATA TOOLS");
        StringAssert.Contains(dataTools.ToString(), "OpenReplayDiagnosticsCommand");
        StringAssert.Contains(dataTools.ToString(), "Open Replay and Diagnostics within Data and Settings");
    }

    [TestMethod]
    public void Navigation_style_exposes_persistent_selected_and_automation_state()
    {
        var xaml = File.ReadAllText(GetAppFile("MainWindow.xaml"));

        StringAssert.Contains(xaml, "NavigationSelectionStyle");
        StringAssert.Contains(xaml, "Property=\"IsChecked\" Value=\"True\"");
        StringAssert.Contains(xaml, "AutomationProperties.ItemStatus");
        StringAssert.Contains(xaml, "SelectedIndicator");
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

    private static XElement FindNamedElement(
        XDocument document,
        XName elementName,
        XNamespace xamlNamespace,
        string name)
    {
        return document
            .Descendants(elementName)
            .Single(element => (string?)element.Attribute(xamlNamespace + "Name") == name);
    }

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
