using System.Reflection;

namespace ApexLab.Application.Identity;

public static class ApplicationIdentity
{
    public static string ProductName { get; } = "ApexLab";

    public static string InformationalVersion { get; } = ResolveInformationalVersion();

    private static string ResolveInformationalVersion()
    {
        var assembly = typeof(ApplicationIdentity).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return informationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }
}
