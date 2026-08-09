using ApexLab.Protocols.F125.Decoding;
using ApexLab.Telemetry.Abstractions.Canonical;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Canonical;

public sealed class F125BahrainCanonicalProjector : ICanonicalPacketProjector
{
    private static readonly F125TelemetryProtocolAdapter Adapter = new();

    public string ProtocolId => F125Protocol.Id;

    public string ContractId => "apexlab-bahrain-tt-slice-v1";

    public string DecoderId => F125BahrainPacketDecoder.DecoderId;

    public string CanonicalSchemaId => "apexlab-canonical-sample-v1";

    public CanonicalProjectionResult Project(ReadOnlySpan<byte> datagram)
    {
        var inspection = Adapter.Inspect(datagram);
        if (!inspection.IsCompatible)
        {
            return CanonicalProjectionResult.Rejected(
                MapClassification(inspection.Classification),
                inspection.Header?.PacketId);
        }

        return inspection.Header!.Value.PacketId switch
        {
            0 => ProjectMotion(
                F125BahrainPacketDecoder.DecodeMotion(datagram)),
            1 => ProjectSession(
                F125BahrainPacketDecoder.DecodeSession(datagram)),
            2 => ProjectLap(
                F125BahrainPacketDecoder.DecodeLapData(datagram)),
            3 => ProjectEvent(
                F125BahrainPacketDecoder.DecodeEvent(datagram)),
            6 => ProjectCarTelemetry(
                F125BahrainPacketDecoder.DecodeCarTelemetry(datagram)),
            var packetId => CanonicalProjectionResult.Excluded(
                packetId,
                CanonicalProjectionReason.CompatibleFamilyOutsideSlice),
        };
    }

    private static CanonicalProjectionResult ProjectMotion(
        F125DecodeResult<F125MotionPlayerData> result)
    {
        if (!result.IsDecoded)
        {
            return Rejected(result.Reason, result.Header?.PacketId);
        }

        var value = result.Value!.Value;
        return CanonicalProjectionResult.Projected(
            CanonicalPacket.CreateMotion(
                Header(value.Header),
                new CanonicalMotionPacket(
                    value.WorldPositionXMetres,
                    value.WorldPositionYMetres,
                    value.WorldPositionZMetres)));
    }

    private static CanonicalProjectionResult ProjectSession(
        F125DecodeResult<F125SessionData> result)
    {
        if (!result.IsDecoded)
        {
            return Rejected(result.Reason, result.Header?.PacketId);
        }

        var value = result.Value!.Value;
        return CanonicalProjectionResult.Projected(
            CanonicalPacket.CreateSession(
                Header(value.Header),
                new CanonicalSessionPacket(
                    value.Weather,
                    value.TrackTemperatureCelsius,
                    value.AirTemperatureCelsius,
                    value.TrackLengthMetres,
                    value.SessionType,
                    value.TrackId,
                    value.Formula,
                    value.IsSpectating,
                    value.IsNetworkGame,
                    value.SteeringAssist,
                    value.BrakingAssist,
                    value.GearboxAssist,
                    value.PitAssist,
                    value.PitReleaseAssist,
                    value.ErsAssist,
                    value.DrsAssist,
                    value.DynamicRacingLine,
                    value.DynamicRacingLineType,
                    value.GameMode,
                    value.RuleSet,
                    value.TimeOfDayMinutesSinceMidnight,
                    value.EqualCarPerformance,
                    value.RecoveryMode)));
    }

    private static CanonicalProjectionResult ProjectLap(
        F125DecodeResult<F125LapPlayerData> result)
    {
        if (!result.IsDecoded)
        {
            return Rejected(result.Reason, result.Header?.PacketId);
        }

        var value = result.Value!.Value;
        return CanonicalProjectionResult.Projected(
            CanonicalPacket.CreateLapData(
                Header(value.Header),
                new CanonicalLapPacket(
                    value.LastLapTimeMilliseconds,
                    value.CurrentLapTimeMilliseconds,
                    value.LapDistanceMetres,
                    value.TotalDistanceMetres,
                    value.CurrentLapNumber,
                    value.PitStatus,
                    value.Sector,
                    value.CurrentLapInvalid,
                    value.DriverStatus,
                    value.ResultStatus)));
    }

    private static CanonicalProjectionResult ProjectEvent(
        F125DecodeResult<F125EventData> result)
    {
        if (result.IsIgnored)
        {
            return CanonicalProjectionResult.Excluded(
                result.Header!.Value.PacketId,
                CanonicalProjectionReason.EventCodeOutsideSlice);
        }

        if (!result.IsDecoded)
        {
            return Rejected(result.Reason, result.Header?.PacketId);
        }

        var value = result.Value!.Value;
        var canonicalEvent = value.Kind switch
        {
            F125SliceEventKind.SessionStarted =>
                CanonicalEventPacket.SessionStarted(),
            F125SliceEventKind.SessionEnded =>
                CanonicalEventPacket.SessionEnded(),
            F125SliceEventKind.Flashback =>
                CanonicalEventPacket.Flashback(
                    value.FlashbackFrameIdentifier!.Value,
                    value.FlashbackSessionTimeSeconds!.Value),
            _ => throw new InvalidOperationException(
                "The F1 decoder returned an unspecified selected Event."),
        };
        return CanonicalProjectionResult.Projected(
            CanonicalPacket.CreateEvent(
                Header(value.Header),
                canonicalEvent));
    }

    private static CanonicalProjectionResult ProjectCarTelemetry(
        F125DecodeResult<F125CarTelemetryPlayerData> result)
    {
        if (!result.IsDecoded)
        {
            return Rejected(result.Reason, result.Header?.PacketId);
        }

        var value = result.Value!.Value;
        return CanonicalProjectionResult.Projected(
            CanonicalPacket.CreateCarTelemetry(
                Header(value.Header),
                new CanonicalCarTelemetryPacket(
                    value.SpeedKilometresPerHour,
                    value.ThrottleRatio,
                    value.BrakeRatio,
                    value.Gear)));
    }

    private static CanonicalPacketHeader Header(
        TelemetryHeaderMetadata header) =>
        new(
            header.SessionTimeSeconds,
            header.FrameIdentifier,
            header.OverallFrameIdentifier,
            header.PlayerCarIndex,
            header.SecondaryPlayerCarIndex);

    private static CanonicalProjectionResult Rejected(
        F125DecodeReason reason,
        byte? packetId) =>
        CanonicalProjectionResult.Rejected(
            MapDecodeReason(reason),
            packetId);

    private static CanonicalProjectionReason MapClassification(
        TelemetryPacketClassification classification) =>
        classification switch
        {
            TelemetryPacketClassification.MalformedHeader =>
                CanonicalProjectionReason.MalformedHeader,
            TelemetryPacketClassification.UnsupportedFormat =>
                CanonicalProjectionReason.UnsupportedFormat,
            TelemetryPacketClassification.UnsupportedYear =>
                CanonicalProjectionReason.UnsupportedYear,
            TelemetryPacketClassification.UnknownPacketId =>
                CanonicalProjectionReason.UnknownPacketId,
            TelemetryPacketClassification.UnsupportedPacketVersion =>
                CanonicalProjectionReason.UnsupportedPacketVersion,
            TelemetryPacketClassification.InvalidPacketLength =>
                CanonicalProjectionReason.InvalidPacketLength,
            TelemetryPacketClassification.ExcludedPrivacyPacket =>
                CanonicalProjectionReason.PrivacyExcludedPacket,
            _ => throw new InvalidOperationException(
                "The F1 adapter returned an invalid projection classification."),
        };

    private static CanonicalProjectionReason MapDecodeReason(
        F125DecodeReason reason) =>
        reason switch
        {
            F125DecodeReason.MalformedHeader =>
                CanonicalProjectionReason.MalformedHeader,
            F125DecodeReason.UnsupportedFormat =>
                CanonicalProjectionReason.UnsupportedFormat,
            F125DecodeReason.UnsupportedYear =>
                CanonicalProjectionReason.UnsupportedYear,
            F125DecodeReason.UnknownPacketId =>
                CanonicalProjectionReason.UnknownPacketId,
            F125DecodeReason.UnexpectedPacketFamily =>
                CanonicalProjectionReason.UnexpectedPacketFamily,
            F125DecodeReason.UnsupportedPacketVersion =>
                CanonicalProjectionReason.UnsupportedPacketVersion,
            F125DecodeReason.InvalidPacketLength =>
                CanonicalProjectionReason.InvalidPacketLength,
            F125DecodeReason.PrivacyExcludedPacket =>
                CanonicalProjectionReason.PrivacyExcludedPacket,
            F125DecodeReason.InvalidPlayerCarIndex =>
                CanonicalProjectionReason.InvalidPlayerCarIndex,
            F125DecodeReason.MalformedSelectedField =>
                CanonicalProjectionReason.MalformedSelectedField,
            _ => throw new InvalidOperationException(
                "The F1 decoder returned an invalid projection reason."),
        };
}
