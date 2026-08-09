using ApexLab.Telemetry.Abstractions.Protocol;
using ApexLab.Protocols.F125.Parsing;

namespace ApexLab.Protocols.F125.Decoding;

public static class F125BahrainPacketDecoder
{
    public static string DecoderId => "f125-v3-minimal-decoder-v1";

    private const byte MaximumPlayerCarIndex = 21;

    private static readonly F125TelemetryProtocolAdapter Adapter = new();

    public static F125DecodeResult<F125MotionPlayerData> DecodeMotion(
        ReadOnlySpan<byte> datagram)
    {
        const byte packetId = 0;
        const int arrayBaseOffset = 29;
        const int stride = 60;

        var gate = Validate(datagram, packetId);
        if (!gate.IsValid)
        {
            return RejectGate<F125MotionPlayerData>(gate);
        }

        var header = gate.Header!.Value;
        var memberBase = checked(
            arrayBaseOffset + (header.PlayerCarIndex * stride));
        if (!LittleEndianFieldReader.TryReadSingle(
                datagram,
                memberBase,
                out var worldPositionXMetres)
            || !LittleEndianFieldReader.TryReadSingle(
                datagram,
                memberBase + 4,
                out var worldPositionYMetres)
            || !LittleEndianFieldReader.TryReadSingle(
                datagram,
                memberBase + 8,
                out var worldPositionZMetres)
            || !float.IsFinite(worldPositionXMetres)
            || !float.IsFinite(worldPositionYMetres)
            || !float.IsFinite(worldPositionZMetres))
        {
            return F125DecodeResult<F125MotionPlayerData>.Rejected(
                F125DecodeReason.MalformedSelectedField,
                header);
        }

        return F125DecodeResult<F125MotionPlayerData>.Decoded(
            new F125MotionPlayerData(
                header,
                worldPositionXMetres,
                worldPositionYMetres,
                worldPositionZMetres),
            header);
    }

    internal static F125DecodeGateResult Validate(
        ReadOnlySpan<byte> datagram,
        byte expectedPacketId)
    {
        var inspection = Adapter.Inspect(datagram);
        if (!inspection.IsCompatible)
        {
            return F125DecodeGateResult.Rejected(
                MapReason(inspection.Classification),
                inspection.Header);
        }

        var header = inspection.Header!.Value;
        if (header.PacketId != expectedPacketId)
        {
            return F125DecodeGateResult.Rejected(
                F125DecodeReason.UnexpectedPacketFamily,
                header);
        }

        if (header.PlayerCarIndex > MaximumPlayerCarIndex)
        {
            return F125DecodeGateResult.Rejected(
                F125DecodeReason.InvalidPlayerCarIndex,
                header);
        }

        return F125DecodeGateResult.Valid(header);
    }

    private static F125DecodeReason MapReason(
        TelemetryPacketClassification classification) =>
        classification switch
        {
            TelemetryPacketClassification.MalformedHeader =>
                F125DecodeReason.MalformedHeader,
            TelemetryPacketClassification.UnsupportedFormat =>
                F125DecodeReason.UnsupportedFormat,
            TelemetryPacketClassification.UnsupportedYear =>
                F125DecodeReason.UnsupportedYear,
            TelemetryPacketClassification.UnknownPacketId =>
                F125DecodeReason.UnknownPacketId,
            TelemetryPacketClassification.UnsupportedPacketVersion =>
                F125DecodeReason.UnsupportedPacketVersion,
            TelemetryPacketClassification.InvalidPacketLength =>
                F125DecodeReason.InvalidPacketLength,
            TelemetryPacketClassification.ExcludedPrivacyPacket =>
                F125DecodeReason.PrivacyExcludedPacket,
            _ => throw new InvalidOperationException(
                "The structural adapter returned an invalid decoder-gate classification."),
        };

    private static F125DecodeResult<TPacket> RejectGate<TPacket>(
        F125DecodeGateResult gate)
        where TPacket : struct
    {
        return F125DecodeResult<TPacket>.Rejected(
            gate.Reason,
            gate.Header);
    }
}

internal readonly record struct F125DecodeGateResult
{
    private F125DecodeGateResult(
        bool isValid,
        F125DecodeReason reason,
        TelemetryHeaderMetadata? header)
    {
        IsValid = isValid;
        Reason = reason;
        Header = header;
    }

    public bool IsValid { get; }

    public F125DecodeReason Reason { get; }

    public TelemetryHeaderMetadata? Header { get; }

    public static F125DecodeGateResult Valid(
        TelemetryHeaderMetadata header) =>
        new(
            isValid: true,
            F125DecodeReason.None,
            header);

    public static F125DecodeGateResult Rejected(
        F125DecodeReason reason,
        TelemetryHeaderMetadata? header) =>
        new(
            isValid: false,
            reason,
            header);
}
