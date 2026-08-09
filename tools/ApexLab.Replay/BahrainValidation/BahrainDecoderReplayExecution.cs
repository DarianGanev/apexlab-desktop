using ApexLab.Application.Capture;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Protocols.F125.Decoding;

namespace ApexLab.Replay.BahrainValidation;

internal static class BahrainDecoderReplayExecution
{
    public static async Task<BahrainDecoderReplayResult> ExecuteAsync(
        ApplicationPaths paths,
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(captureId);
        cancellationToken.ThrowIfCancellationRequested();

        RawEvidenceCapture? unownedCapture = null;
        try
        {
            unownedCapture = await RawEvidenceReader.OpenAsync(
                paths,
                captureId,
                RawEvidenceProtocolId.Parse(F125Protocol.Id),
                cancellationToken).ConfigureAwait(false);

            await using var source = new RawReplayDatagramSource(
                unownedCapture,
                new RawReplayOptions(RawReplayTimingMode.Immediate));
            unownedCapture = null;
            var result = BahrainDecoderReplayResult.Empty;
            await source.StartAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var envelope in source.Output.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!SenderPolicy.LoopbackOnly.IsExpected(envelope.Sender))
                {
                    throw new InvalidDataException(
                        "Decoder validation accepts loopback evidence only.");
                }

                result = Evaluate(result, envelope.Payload.Span);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            if (unownedCapture is not null)
            {
                try
                {
                    await unownedCapture.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Reader disposal already attempts every retained handle.
                }
            }
        }
    }

    private static BahrainDecoderReplayResult Evaluate(
        BahrainDecoderReplayResult aggregate,
        ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length <= 6)
        {
            throw new InvalidDataException(
                "Evidence contains a packet without a complete family header.");
        }

        return datagram[6] switch
        {
            0 => Apply(
                aggregate,
                F125BahrainPacketDecoder.DecodeMotion(datagram).Disposition,
                static value => value with { MotionDecoded = true }),
            1 => Apply(
                aggregate,
                F125BahrainPacketDecoder.DecodeSession(datagram).Disposition,
                static value => value with { SessionDecoded = true }),
            2 => Apply(
                aggregate,
                F125BahrainPacketDecoder.DecodeLapData(datagram).Disposition,
                static value => value with { LapDecoded = true }),
            3 => Apply(
                aggregate,
                F125BahrainPacketDecoder.DecodeEvent(datagram).Disposition,
                static value => value with { EventDecoded = true }),
            6 => Apply(
                aggregate,
                F125BahrainPacketDecoder.DecodeCarTelemetry(datagram).Disposition,
                static value => value with { CarTelemetryDecoded = true }),
            _ => ValidateIgnoredFamily(aggregate, datagram),
        };
    }

    private static BahrainDecoderReplayResult Apply(
        BahrainDecoderReplayResult aggregate,
        F125DecodeDisposition disposition,
        Func<BahrainDecoderReplayResult, BahrainDecoderReplayResult> markDecoded)
    {
        return disposition switch
        {
            F125DecodeDisposition.Decoded => markDecoded(aggregate),
            F125DecodeDisposition.Ignored => aggregate,
            F125DecodeDisposition.Rejected => aggregate with
            {
                SelectedPacketsRejected = true,
            },
            _ => throw new InvalidDataException(
                "The decoder returned an unspecified disposition."),
        };
    }

    private static BahrainDecoderReplayResult ValidateIgnoredFamily(
        BahrainDecoderReplayResult aggregate,
        ReadOnlySpan<byte> datagram)
    {
        var inspection = new F125TelemetryProtocolAdapter().Inspect(datagram);
        if (!inspection.IsCompatible)
        {
            throw new InvalidDataException(
                "Evidence contains an incompatible packet outside the selected slice.");
        }

        return aggregate;
    }
}

internal readonly record struct BahrainDecoderReplayResult(
    bool MotionDecoded,
    bool SessionDecoded,
    bool LapDecoded,
    bool EventDecoded,
    bool CarTelemetryDecoded,
    bool SelectedPacketsRejected)
{
    public static BahrainDecoderReplayResult Empty => new(
        MotionDecoded: false,
        SessionDecoded: false,
        LapDecoded: false,
        EventDecoded: false,
        CarTelemetryDecoded: false,
        SelectedPacketsRejected: false);

    public static BahrainDecoderReplayResult Complete => new(
        MotionDecoded: true,
        SessionDecoded: true,
        LapDecoded: true,
        EventDecoded: true,
        CarTelemetryDecoded: true,
        SelectedPacketsRejected: false);

    public bool HasAllContinuousFamilies =>
        MotionDecoded
        && SessionDecoded
        && LapDecoded
        && CarTelemetryDecoded;
}
