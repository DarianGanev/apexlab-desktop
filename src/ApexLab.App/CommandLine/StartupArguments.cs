using System.IO;
using ApexLab.Application.Storage;

namespace ApexLab.App.CommandLine;

public enum StartupMode
{
    Interactive,
    SmokeTest,
}

public enum StartupArgumentErrorCode
{
    UnknownOption,
    UnexpectedToken,
    DuplicateOption,
    IncompleteSmokeTest,
    SmokeTestRequired,
    InvalidPath,
    RelativePath,
    ResultFileMustBeJson,
}

public sealed record StartupArgumentError(StartupArgumentErrorCode Code, string Message);

public sealed record StartupArgumentsParseResult(
    StartupArguments? Value,
    StartupArgumentError? Error)
{
    public bool IsSuccess => Value is not null;
}

public sealed record StartupArguments
{
    private StartupArguments(StartupMode mode, string? dataRoot, string? resultFile)
    {
        Mode = mode;
        DataRoot = dataRoot;
        ResultFile = resultFile;
    }

    public StartupMode Mode { get; }

    public string? DataRoot { get; }

    public string? ResultFile { get; }

    public bool CanShowMainWindow => Mode == StartupMode.Interactive;

    public static StartupArgumentsParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0)
        {
            return Success(new StartupArguments(StartupMode.Interactive, null, null));
        }

        var duplicate = FindDuplicateOption(arguments);
        if (duplicate is not null)
        {
            return Failure(
                StartupArgumentErrorCode.DuplicateOption,
                $"Option '{duplicate}' may be specified only once.");
        }

        var invalidToken = FindUnexpectedPositionalToken(arguments);
        if (invalidToken is not null)
        {
            return Failure(StartupArgumentErrorCode.UnexpectedToken, "Positional startup arguments are not supported.");
        }

        var unknown = arguments.FirstOrDefault(argument =>
            argument.StartsWith("--", StringComparison.Ordinal) && !KnownOptions.Contains(argument));
        if (unknown is not null)
        {
            return Failure(StartupArgumentErrorCode.UnknownOption, $"Unknown option '{unknown}'.");
        }

        if (!arguments.Contains("--smoke-test", StringComparer.Ordinal))
        {
            return Failure(
                StartupArgumentErrorCode.SmokeTestRequired,
                "Data and result paths are valid only with '--smoke-test'.");
        }

        if (arguments.Count != 5
            || arguments[0] != "--smoke-test"
            || arguments[1] != "--data-root"
            || arguments[3] != "--result-file")
        {
            return Failure(
                StartupArgumentErrorCode.IncompleteSmokeTest,
                "Smoke mode requires '--smoke-test --data-root <absolute-path> --result-file <absolute-json-path>'.");
        }

        var dataRootResult = NormalizeDataRoot(arguments[2]);
        if (dataRootResult.Error is not null)
        {
            return new StartupArgumentsParseResult(null, dataRootResult.Error);
        }

        var resultFileResult = NormalizeResultFile(arguments[4]);
        if (resultFileResult.Error is not null)
        {
            return new StartupArgumentsParseResult(null, resultFileResult.Error);
        }

        return Success(new StartupArguments(
            StartupMode.SmokeTest,
            dataRootResult.Path,
            resultFileResult.Path));
    }

    private static readonly HashSet<string> KnownOptions = new(StringComparer.Ordinal)
    {
        "--smoke-test",
        "--data-root",
        "--result-file",
    };

    private static string? FindDuplicateOption(IReadOnlyList<string> arguments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            if (KnownOptions.Contains(argument) && !seen.Add(argument))
            {
                return argument;
            }
        }

        return null;
    }

    private static string? FindUnexpectedPositionalToken(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var token = arguments[index];
            if (!token.StartsWith("--", StringComparison.Ordinal)
                && (index == 0 || arguments[index - 1] is not ("--data-root" or "--result-file")))
            {
                return token;
            }
        }

        return null;
    }

    private static PathResult NormalizeDataRoot(string path)
    {
        var basicFailure = ValidateBasicPath(path, "Data root");
        if (basicFailure is not null)
        {
            return new PathResult(null, basicFailure);
        }

        try
        {
            return new PathResult(ApplicationPaths.FromRoot(path).RootDirectory, null);
        }
        catch (ArgumentException)
        {
            return InvalidPath("Data root is not a safe absolute local directory path.");
        }
    }

    private static PathResult NormalizeResultFile(string path)
    {
        var basicFailure = ValidateBasicPath(path, "Result file");
        if (basicFailure is not null)
        {
            return new PathResult(null, basicFailure);
        }

        var fileName = Path.GetFileName(path);
        if (!string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(path)))
        {
            return new PathResult(
                null,
                new StartupArgumentError(
                    StartupArgumentErrorCode.ResultFileMustBeJson,
                    "Result file must be an absolute path ending in '.json'."));
        }

        if (OperatingSystem.IsWindows() && !WindowsPathSegment.IsSafe(fileName))
        {
            return InvalidPath("Result file contains an unsafe or non-canonical Windows file name.");
        }

        try
        {
            var normalized = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(normalized);
            if (parent is null)
            {
                return InvalidPath("Result file must have a safe absolute local parent directory.");
            }

            _ = ApplicationPaths.FromRoot(parent);
            if (Path.GetFileName(normalized).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return InvalidPath("Result file contains invalid path characters.");
            }

            return new PathResult(normalized, null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return InvalidPath("Result file is not a valid path.");
        }
    }

    private static StartupArgumentError? ValidateBasicPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new StartupArgumentError(
                StartupArgumentErrorCode.InvalidPath,
                $"{label} path must not be empty.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return new StartupArgumentError(
                StartupArgumentErrorCode.RelativePath,
                $"{label} path must be absolute.");
        }

        return null;
    }

    private static PathResult InvalidPath(string message) =>
        new(null, new StartupArgumentError(StartupArgumentErrorCode.InvalidPath, message));

    private static StartupArgumentsParseResult Success(StartupArguments value) => new(value, null);

    private static StartupArgumentsParseResult Failure(StartupArgumentErrorCode code, string message) =>
        new(null, new StartupArgumentError(code, message));

    private sealed record PathResult(string? Path, StartupArgumentError? Error);
}
