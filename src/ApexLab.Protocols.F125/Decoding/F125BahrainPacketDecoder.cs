using ApexLab.Telemetry.Abstractions.Protocol;
using ApexLab.Protocols.F125.Parsing;

namespace ApexLab.Protocols.F125.Decoding;

public static class F125BahrainPacketDecoder
{
    public static string DecoderId => "f125-v3-minimal-decoder-v1";

    private const byte MaximumPlayerCarIndex = 21;

    private static readonly F125TelemetryProtocolAdapter Adapter = new();

    public static F125DecodeResult<F125LapPlayerData> DecodeLapData(
        ReadOnlySpan<byte> datagram)
    {
        const byte packetId = 2;
        const int arrayBaseOffset = 29;
        const int stride = 57;

        var gate = Validate(datagram, packetId);
        if (!gate.IsValid)
        {
            return RejectGate<F125LapPlayerData>(gate);
        }

        var header = gate.Header!.Value;
        var memberBase = checked(
            arrayBaseOffset + (header.PlayerCarIndex * stride));
        if (!LittleEndianFieldReader.TryReadUInt32(
                datagram,
                memberBase,
                out var lastLapTimeMilliseconds)
            || !LittleEndianFieldReader.TryReadUInt32(
                datagram,
                memberBase + 4,
                out var currentLapTimeMilliseconds)
            || !LittleEndianFieldReader.TryReadSingle(
                datagram,
                memberBase + 20,
                out var lapDistanceMetres)
            || !LittleEndianFieldReader.TryReadSingle(
                datagram,
                memberBase + 24,
                out var totalDistanceMetres)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                memberBase + 33,
                out var currentLapNumber)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                memberBase + 34,
                out var pitStatus)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                memberBase + 36,
                out var sector)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                memberBase + 37,
                out var currentLapInvalid)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                memberBase + 44,
                out var driverStatus)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                memberBase + 45,
                out var resultStatus)
            || !float.IsFinite(lapDistanceMetres)
            || !float.IsFinite(totalDistanceMetres)
            || pitStatus > 2
            || sector > 2
            || !IsBinary(currentLapInvalid)
            || driverStatus > 4
            || resultStatus > 7)
        {
            return F125DecodeResult<F125LapPlayerData>.Rejected(
                F125DecodeReason.MalformedSelectedField,
                header);
        }

        return F125DecodeResult<F125LapPlayerData>.Decoded(
            new F125LapPlayerData(
                header,
                lastLapTimeMilliseconds,
                currentLapTimeMilliseconds,
                lapDistanceMetres,
                totalDistanceMetres,
                currentLapNumber,
                pitStatus,
                sector,
                currentLapInvalid == 1,
                driverStatus,
                resultStatus),
            header);
    }

    public static F125DecodeResult<F125SessionData> DecodeSession(
        ReadOnlySpan<byte> datagram)
    {
        const byte packetId = 1;

        var gate = Validate(datagram, packetId);
        if (!gate.IsValid)
        {
            return RejectGate<F125SessionData>(gate);
        }

        var header = gate.Header!.Value;
        if (!LittleEndianFieldReader.TryReadByte(datagram, 29, out var weather)
            || !LittleEndianFieldReader.TryReadSByte(
                datagram,
                30,
                out var trackTemperatureCelsius)
            || !LittleEndianFieldReader.TryReadSByte(
                datagram,
                31,
                out var airTemperatureCelsius)
            || !LittleEndianFieldReader.TryReadUInt16(
                datagram,
                33,
                out var trackLengthMetres)
            || !LittleEndianFieldReader.TryReadByte(datagram, 35, out var sessionType)
            || !LittleEndianFieldReader.TryReadSByte(datagram, 36, out var trackId)
            || !LittleEndianFieldReader.TryReadByte(datagram, 37, out var formula)
            || !LittleEndianFieldReader.TryReadByte(datagram, 44, out var isSpectating)
            || !LittleEndianFieldReader.TryReadByte(datagram, 154, out var networkGame)
            || !LittleEndianFieldReader.TryReadByte(datagram, 685, out var steeringAssist)
            || !LittleEndianFieldReader.TryReadByte(datagram, 686, out var brakingAssist)
            || !LittleEndianFieldReader.TryReadByte(datagram, 687, out var gearboxAssist)
            || !LittleEndianFieldReader.TryReadByte(datagram, 688, out var pitAssist)
            || !LittleEndianFieldReader.TryReadByte(datagram, 689, out var pitReleaseAssist)
            || !LittleEndianFieldReader.TryReadByte(datagram, 690, out var ersAssist)
            || !LittleEndianFieldReader.TryReadByte(datagram, 691, out var drsAssist)
            || !LittleEndianFieldReader.TryReadByte(datagram, 692, out var dynamicRacingLine)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                693,
                out var dynamicRacingLineType)
            || !LittleEndianFieldReader.TryReadByte(datagram, 694, out var gameMode)
            || !LittleEndianFieldReader.TryReadByte(datagram, 695, out var ruleSet)
            || !LittleEndianFieldReader.TryReadUInt32(
                datagram,
                696,
                out var timeOfDayMinutesSinceMidnight)
            || !LittleEndianFieldReader.TryReadByte(
                datagram,
                708,
                out var equalCarPerformance)
            || !LittleEndianFieldReader.TryReadByte(datagram, 709, out var recoveryMode)
            || weather > 5
            || !IsBinary(isSpectating)
            || !IsBinary(networkGame)
            || steeringAssist > 1
            || brakingAssist > 3
            || gearboxAssist is < 1 or > 3
            || pitAssist > 1
            || pitReleaseAssist > 1
            || ersAssist > 1
            || drsAssist > 1
            || dynamicRacingLine > 2
            || dynamicRacingLineType > 1
            || timeOfDayMinutesSinceMidnight >= 1440
            || !IsBinary(equalCarPerformance)
            || recoveryMode > 2)
        {
            return F125DecodeResult<F125SessionData>.Rejected(
                F125DecodeReason.MalformedSelectedField,
                header);
        }

        return F125DecodeResult<F125SessionData>.Decoded(
            new F125SessionData(
                header,
                weather,
                trackTemperatureCelsius,
                airTemperatureCelsius,
                trackLengthMetres,
                sessionType,
                trackId,
                formula,
                isSpectating == 1,
                networkGame == 1,
                steeringAssist,
                brakingAssist,
                gearboxAssist,
                pitAssist,
                pitReleaseAssist,
                ersAssist,
                drsAssist,
                dynamicRacingLine,
                dynamicRacingLineType,
                gameMode,
                ruleSet,
                timeOfDayMinutesSinceMidnight,
                equalCarPerformance == 1,
                recoveryMode),
            header);
    }

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

    private static bool IsBinary(byte value) => value <= 1;
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
