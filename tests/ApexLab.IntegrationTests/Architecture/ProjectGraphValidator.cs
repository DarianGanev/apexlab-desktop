using System.Xml.Linq;

namespace ApexLab.IntegrationTests.Architecture;

internal static class ProjectGraphValidator
{
    private static readonly string[] ForbiddenPureProjectPackageTerms =
    [
        "FileSystem",
        "Hosting",
        "Socket",
        "SQLite",
        "System.IO",
        "WPF",
    ];

    private static readonly ISet<string> PureProjects = ProjectSet(
        "ApexLab.Analysis",
        "ApexLab.Application",
        "ApexLab.Coaching",
        "ApexLab.Domain",
        "ApexLab.Telemetry.Abstractions");

    private static readonly IReadOnlyDictionary<string, ISet<string>> AllowedReferences =
        new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ApexLab.App"] = ProjectSet(
                "ApexLab.Application",
                "ApexLab.Persistence",
                "ApexLab.Protocols.F125",
                "ApexLab.Telemetry"),
            ["ApexLab.Application"] = ProjectSet(
                "ApexLab.Analysis",
                "ApexLab.Coaching",
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Domain"] = ProjectSet(),
            ["ApexLab.Persistence"] = ProjectSet(
                "ApexLab.Application",
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Protocols.F125"] = ProjectSet(
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Telemetry"] = ProjectSet(
                "ApexLab.Application",
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Telemetry.Abstractions"] = ProjectSet(),
        };

    private static readonly IReadOnlyDictionary<string, ISet<string>> RequiredReferences =
        new Dictionary<string, ISet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ApexLab.App"] = ProjectSet(
                "ApexLab.Application",
                "ApexLab.Persistence",
                "ApexLab.Protocols.F125",
                "ApexLab.Telemetry"),
            ["ApexLab.Application"] = ProjectSet(
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Domain"] = ProjectSet(),
            ["ApexLab.Persistence"] = ProjectSet(
                "ApexLab.Application",
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Protocols.F125"] = ProjectSet(
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Telemetry"] = ProjectSet(
                "ApexLab.Application",
                "ApexLab.Domain",
                "ApexLab.Telemetry.Abstractions"),
            ["ApexLab.Telemetry.Abstractions"] = ProjectSet(),
        };

    public static IReadOnlyList<string> FindViolations(
        IReadOnlyDictionary<string, string> projectFiles)
    {
        var violations = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (projectPath, projectXml) in projectFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(projectPath);
            var document = XDocument.Parse(projectXml);

            if (PureProjects.Contains(projectName))
            {
                AddPureProjectInfrastructureViolations(projectName, document, violations);
            }

            var referencedProjects = document
                .Descendants("ProjectReference")
                .Select(reference => reference.Attribute("Include")?.Value)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFileNameWithoutExtension(path!))
                .ToList();

            if (!IsTestProject(projectName))
            {
                foreach (var referencedTestProject in referencedProjects.Where(IsTestProject))
                {
                    violations.Add($"{projectName} -> {referencedTestProject}");
                }

                if (!projectName.Equals("ApexLab.App", StringComparison.OrdinalIgnoreCase) &&
                    referencedProjects.Contains(
                        "ApexLab.App",
                        StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add($"{projectName} -> ApexLab.App");
                }
            }

            if (!AllowedReferences.TryGetValue(projectName, out var allowedReferences))
            {
                continue;
            }

            foreach (var referencedProject in referencedProjects)
            {
                if (!allowedReferences.Contains(referencedProject))
                {
                    violations.Add($"{projectName} -> {referencedProject}");
                }
            }

            foreach (var requiredReference in RequiredReferences[projectName])
            {
                if (!referencedProjects.Contains(
                    requiredReference,
                    StringComparer.OrdinalIgnoreCase))
                {
                    violations.Add($"{projectName} -/-> {requiredReference}");
                }
            }
        }

        return violations.Order(StringComparer.Ordinal).ToList();
    }

    private static void AddPureProjectInfrastructureViolations(
        string projectName,
        XDocument document,
        ICollection<string> violations)
    {
        var usesWindowsDesktopSdk = string.Equals(
            document.Root?.Attribute("Sdk")?.Value,
            "Microsoft.NET.Sdk.WindowsDesktop",
            StringComparison.OrdinalIgnoreCase);
        var usesWpfFrameworkReference = document
            .Descendants("FrameworkReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Any(framework => string.Equals(
                framework,
                "Microsoft.WindowsDesktop.App.WPF",
                StringComparison.OrdinalIgnoreCase));
        var usesWpf = document
            .Descendants("UseWPF")
            .Any(element => string.Equals(
                element.Value.Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase));

        if (usesWindowsDesktopSdk || usesWpfFrameworkReference || usesWpf)
        {
            violations.Add($"{projectName} -> framework WPF");
        }

        var packages = document
            .Descendants("PackageReference")
            .Select(reference => reference.Attribute("Include")?.Value)
            .Where(package => !string.IsNullOrWhiteSpace(package));

        foreach (var package in packages)
        {
            if (ForbiddenPureProjectPackageTerms.Any(
                term => package!.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                violations.Add($"{projectName} -> package {package}");
            }
        }
    }

    private static ISet<string> ProjectSet(params string[] projects) =>
        new HashSet<string>(projects, StringComparer.OrdinalIgnoreCase);

    private static bool IsTestProject(string projectName) =>
        projectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
        projectName.EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
}
