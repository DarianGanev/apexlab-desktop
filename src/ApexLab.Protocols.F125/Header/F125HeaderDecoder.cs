using ApexLab.Protocols.F125.Parsing;

namespace ApexLab.Protocols.F125.Header;

internal static class F125HeaderDecoder
{
    public static bool TryDecode(ReadOnlySpan<byte> datagram, out F125PacketHeader header)
    {
        header = default;
        if (datagram.Length < F125Protocol.HeaderLength)
        {
            return false;
        }

        var reader = new LittleEndianSpanReader(datagram);
        if (!reader.TryReadUInt16(out var packetFormat)
            || !reader.TryReadByte(out var gameYear)
            || !reader.TryReadByte(out var gameMajorVersion)
            || !reader.TryReadByte(out var gameMinorVersion)
            || !reader.TryReadByte(out var packetVersion)
            || !reader.TryReadByte(out var packetId)
            || !reader.TryReadUInt64(out var sessionUid)
            || !reader.TryReadSingle(out var sessionTimeSeconds)
            || !reader.TryReadUInt32(out var frameIdentifier)
            || !reader.TryReadUInt32(out var overallFrameIdentifier)
            || !reader.TryReadByte(out var playerCarIndex)
            || !reader.TryReadByte(out var secondaryPlayerCarIndex)
            || reader.Position != F125Protocol.HeaderLength
            || !float.IsFinite(sessionTimeSeconds))
        {
            return false;
        }

        header = new F125PacketHeader(
            packetFormat,
            gameYear,
            gameMajorVersion,
            gameMinorVersion,
            packetVersion,
            packetId,
            sessionUid,
            sessionTimeSeconds,
            frameIdentifier,
            overallFrameIdentifier,
            playerCarIndex,
            secondaryPlayerCarIndex);
        return true;
    }
}
