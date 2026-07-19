namespace ApexLab.IntegrationTests.Architecture;

[TestClass]
public sealed class ProjectBoundaryTests
{
    [TestMethod]
    public void Forbidden_project_reference_reports_the_named_edge()
    {
        const string forbiddenEdge = "ApexLab.Domain -> ApexLab.Persistence";
        var projectFiles = new Dictionary<string, string>
        {
            ["src/ApexLab.Domain/ApexLab.Domain.csproj"] =
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../ApexLab.Persistence/ApexLab.Persistence.csproj" />
                  </ItemGroup>
                </Project>
                """,
        };

        var violations = ProjectGraphValidator.FindViolations(projectFiles);

        CollectionAssert.Contains(
            violations.ToList(),
            forbiddenEdge,
            $"Expected violation: {forbiddenEdge}");
    }

    [TestMethod]
    public void Pure_project_reports_forbidden_infrastructure_package()
    {
        const string forbiddenPackage =
            "ApexLab.Application -> package Microsoft.Extensions.Hosting";
        var projectFiles = new Dictionary<string, string>
        {
            ["src/ApexLab.Application/ApexLab.Application.csproj"] =
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="Microsoft.Extensions.Hosting" />
                  </ItemGroup>
                </Project>
                """,
        };

        var violations = ProjectGraphValidator.FindViolations(projectFiles);

        CollectionAssert.Contains(
            violations.ToList(),
            forbiddenPackage,
            $"Expected violation: {forbiddenPackage}");
    }

    [TestMethod]
    public void Production_project_reports_reference_to_test_project()
    {
        const string forbiddenEdge =
            "ApexLab.FutureAdapter -> ApexLab.Domain.Tests";
        var projectFiles = new Dictionary<string, string>
        {
            ["src/ApexLab.FutureAdapter/ApexLab.FutureAdapter.csproj"] =
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../../tests/ApexLab.Domain.Tests/ApexLab.Domain.Tests.csproj" />
                  </ItemGroup>
                </Project>
                """,
        };

        var violations = ProjectGraphValidator.FindViolations(projectFiles);

        CollectionAssert.Contains(
            violations.ToList(),
            forbiddenEdge,
            $"Expected violation: {forbiddenEdge}");
    }

    [TestMethod]
    public void Missing_required_reference_reports_the_named_edge()
    {
        const string missingEdge =
            "ApexLab.Application -/-> ApexLab.Telemetry.Abstractions";
        var projectFiles = new Dictionary<string, string>
        {
            ["src/ApexLab.Application/ApexLab.Application.csproj"] =
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="../ApexLab.Domain/ApexLab.Domain.csproj" />
                  </ItemGroup>
                </Project>
                """,
        };

        var violations = ProjectGraphValidator.FindViolations(projectFiles);

        CollectionAssert.Contains(
            violations.ToList(),
            missingEdge,
            $"Expected violation: {missingEdge}");
    }

    [TestMethod]
    public void Pure_project_reports_wpf_framework_dependency()
    {
        const string forbiddenFramework = "ApexLab.Domain -> framework WPF";
        var projectFiles = new Dictionary<string, string>
        {
            ["src/ApexLab.Domain/ApexLab.Domain.csproj"] =
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <UseWPF>true</UseWPF>
                  </PropertyGroup>
                </Project>
                """,
        };

        var violations = ProjectGraphValidator.FindViolations(projectFiles);

        CollectionAssert.Contains(
            violations.ToList(),
            forbiddenFramework,
            $"Expected violation: {forbiddenFramework}");
    }

    [TestMethod]
    public void Solution_project_graph_respects_boundaries()
    {
        var repositoryRoot = FindRepositoryRoot();
        var projectFiles = Directory
            .EnumerateFiles(repositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => Path.GetRelativePath(repositoryRoot, path),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase);

        var violations = ProjectGraphValidator.FindViolations(projectFiles);

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

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

        throw new DirectoryNotFoundException(
            "Could not find the repository root containing ApexLab.slnx.");
    }
}
