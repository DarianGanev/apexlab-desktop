namespace ApexLab.Replay.Probe;

internal enum ProbeExitCode
{
    Success = 0,
    InvalidArguments = 2,
    BindFailure = 3,
    NoTraffic = 4,
    IncompatibleOnlyTraffic = 5,
    Interrupted = 6,
    UnexpectedFailure = 7,
}

internal readonly record struct ProbeCommandResult(
    ProbeExitCode ExitCode,
    string Json);
