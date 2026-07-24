using ApexLab.Application.Capture;

namespace ApexLab.App.Capture;

public static class CaptureStatusText
{
    public static string For(
        CaptureState state,
        CaptureFailureKind failureKind)
    {
        if (failureKind == CaptureFailureKind.PortConflict)
        {
            return "UDP port 20777 is already in use";
        }

        if (failureKind == CaptureFailureKind.EvidenceWrite)
        {
            return "Evidence could not be written";
        }

        return state switch
        {
            CaptureState.Idle => "Ready to arm",
            CaptureState.Binding => "Binding UDP port 20777",
            CaptureState.WaitingForTraffic =>
                "Waiting for F1 25 telemetry",
            CaptureState.ReceivingCompatibleTraffic =>
                "Receiving compatible F1 25 telemetry",
            CaptureState.IncompatibleTraffic =>
                "Traffic received, but it is not compatible",
            CaptureState.Stopping => "Stopping and draining capture",
            CaptureState.Stopped => "Capture stopped",
            CaptureState.Interrupted =>
                "Cleanup continues in the background",
            CaptureState.Faulted => "Capture needs attention",
            CaptureState.Disposed => "Capture is unavailable",
            _ => "Capture status is unavailable",
        };
    }
}
