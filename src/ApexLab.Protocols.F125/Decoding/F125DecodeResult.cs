using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Decoding;

public readonly record struct F125DecodeResult<TPacket>
    where TPacket : struct
{
    private F125DecodeResult(
        F125DecodeDisposition disposition,
        F125DecodeReason reason,
        TelemetryHeaderMetadata? header,
        TPacket? value)
    {
        Disposition = disposition;
        Reason = reason;
        Header = header;
        Value = value;
    }

    public F125DecodeDisposition Disposition { get; }

    public F125DecodeReason Reason { get; }

    public TelemetryHeaderMetadata? Header { get; }

    public TPacket? Value { get; }

    public bool IsDecoded => Disposition == F125DecodeDisposition.Decoded;

    public bool IsIgnored => Disposition == F125DecodeDisposition.Ignored;

    public bool IsRejected => Disposition == F125DecodeDisposition.Rejected;

    public static F125DecodeResult<TPacket> Decoded(
        TPacket value,
        TelemetryHeaderMetadata header)
    {
        return new(
            F125DecodeDisposition.Decoded,
            F125DecodeReason.None,
            header,
            value);
    }

    public static F125DecodeResult<TPacket> Ignored(
        F125DecodeReason reason,
        TelemetryHeaderMetadata header)
    {
        if (reason != F125DecodeReason.EventCodeOutsideSlice)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Only an out-of-slice Event code can be ignored.");
        }

        return new(
            F125DecodeDisposition.Ignored,
            reason,
            header,
            value: null);
    }

    public static F125DecodeResult<TPacket> Rejected(
        F125DecodeReason reason,
        TelemetryHeaderMetadata? header = null)
    {
        if (reason is F125DecodeReason.None
            or F125DecodeReason.EventCodeOutsideSlice
            || !Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "A known structural decoder rejection reason is required.");
        }

        return new(
            F125DecodeDisposition.Rejected,
            reason,
            header,
            value: null);
    }
}
