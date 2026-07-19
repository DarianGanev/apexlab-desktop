using ApexLab.Application.Identity;

namespace ApexLab.Application.Storage;

public sealed record ApplicationPaths
{
    private ApplicationPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        DatabaseFile = Path.Combine(rootDirectory, "apexlab.db");
        RawCapturesDirectory = Path.Combine(rootDirectory, "captures");
        DerivedCacheDirectory = Path.Combine(rootDirectory, "cache");
        LogsDirectory = Path.Combine(rootDirectory, "logs");
        BackupsDirectory = Path.Combine(rootDirectory, "backups");
        ImportStagingDirectory = Path.Combine(rootDirectory, "import-staging");
    }

    public string RootDirectory { get; }

    public string DatabaseFile { get; }

    public string RawCapturesDirectory { get; }

    public string DerivedCacheDirectory { get; }

    public string LogsDirectory { get; }

    public string BackupsDirectory { get; }

    public string ImportStagingDirectory { get; }

    public static ApplicationPaths FromLocalApplicationData(string localApplicationDataDirectory)
    {
        var normalizedLocalApplicationData = NormalizeDataDirectory(
            localApplicationDataDirectory,
            nameof(localApplicationDataDirectory));
        var rootDirectory = Path.GetFullPath(
            Path.Combine(normalizedLocalApplicationData, ApplicationIdentity.ProductName));

        return new ApplicationPaths(rootDirectory);
    }

    public static ApplicationPaths FromRoot(string rootDirectory)
    {
        return new ApplicationPaths(NormalizeDataDirectory(rootDirectory, nameof(rootDirectory)));
    }

    private static string NormalizeDataDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A data directory is required.", parameterName);
        }

        if (UsesNonLocalOrDeviceNamespace(path))
        {
            throw new ArgumentException(
                "The data directory must use a local filesystem path without a device namespace.",
                parameterName);
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The data directory must be an absolute path.", parameterName);
        }

        if (ContainsTraversalSegment(path))
        {
            throw new ArgumentException("The data directory cannot contain traversal segments.", parameterName);
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The data directory is not a valid path.", parameterName, exception);
        }

        var pathRoot = Path.GetPathRoot(normalizedPath);
        if (!HasCanonicalLocalRoot(pathRoot)
            || string.Equals(
                normalizedPath,
                Path.TrimEndingDirectorySeparator(pathRoot!),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new ArgumentException("A filesystem root cannot be used as the data directory.", parameterName);
        }

        return normalizedPath;
    }

    private static bool UsesNonLocalOrDeviceNamespace(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var windowsPath = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        // ApexLab intentionally keeps its private evidence store on a local drive.
        return windowsPath.StartsWith(@"\\", StringComparison.Ordinal)
            || windowsPath.StartsWith(@"\??\", StringComparison.Ordinal);
    }

    private static bool HasCanonicalLocalRoot(string? pathRoot)
    {
        if (pathRoot is null)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return Path.IsPathFullyQualified(pathRoot);
        }

        return pathRoot.Length == 3
            && char.IsAsciiLetter(pathRoot[0])
            && pathRoot[1] == Path.VolumeSeparatorChar
            && pathRoot[2] == Path.DirectorySeparatorChar;
    }

    private static bool ContainsTraversalSegment(string path)
    {
        return path
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(segment =>
            {
                var normalizedSegment = segment.TrimEnd(' ');
                return normalizedSegment is "." or ".."
                    || OperatingSystem.IsWindows() && segment[^1] is ' ' or '.';
            });
    }
}
