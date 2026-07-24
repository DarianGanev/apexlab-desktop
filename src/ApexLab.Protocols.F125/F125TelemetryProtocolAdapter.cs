using ApexLab.Protocols.F125.Header;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125;

public sealed class F125TelemetryProtocolAdapter : ITelemetryProtocolAdapter
{
    public string ProtocolId => F125Protocol.Id;

    public int HeaderLength => F125Protocol.HeaderLength;

    public TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram)
    {
        if (!F125HeaderDecoder.TryDecode(datagram, out var decodedHeader))
        {
            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.MalformedHeader);
        }

        var header = ToMetadata(decodedHeader);

        if (header.PacketFormat != F125Protocol.PacketFormat)
        {
            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.UnsupportedFormat,
                header);
        }

        if (header.GameYear != F125Protocol.GameYear)
        {
            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.UnsupportedYear,
                header);
        }

        if (!F125PacketDescriptorCatalog.TryGet(header.PacketId, out var descriptor))
        {
            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.UnknownPacketId,
                header);
        }

        if (header.PacketVersion != descriptor.PacketVersion)
        {
            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.UnsupportedPacketVersion,
                header,
                descriptor);
        }

        if (datagram.Length != descriptor.DatagramLength)
        {
            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.InvalidPacketLength,
                header,
                descriptor);
        }

        if (descriptor.PrivacyDisposition
            == TelemetryPacketPrivacyDisposition.IdentityBearingExcluded)
        {
            return TelemetryPacketResult.Rejected(
                TelemetryPacketClassification.ExcludedPrivacyPacket,
                header,
                descriptor);
        }

        return TelemetryPacketResult.Compatible(header, descriptor);
    }

    private static TelemetryHeaderMetadata ToMetadata(F125PacketHeader header)
    {
        return new(
            header.PacketFormat,
            header.GameYear,
            header.GameMajorVersion,
            header.GameMinorVersion,
            header.PacketVersion,
            header.PacketId,
            header.SessionUid,
            header.SessionTimeSeconds,
            header.FrameIdentifier,
            header.OverallFrameIdentifier,
            header.PlayerCarIndex,
            header.SecondaryPlayerCarIndex);
    }
}
