namespace ApexLab.Application.Capture;

public sealed class CaptureSourceStartupException : Exception
{
    public CaptureSourceStartupException(Exception innerException)
        : base("The telemetry datagram source failed to start.", innerException)
    {
        ArgumentNullException.ThrowIfNull(innerException);
    }
}
