namespace ApexLab.Protocols.F125.Tests.Decoding;

internal static class F125TestDatagramBuilder
{
    public static byte[] Create(
        byte packetId,
        int length,
        byte packetVersion = 1,
        ushort packetFormat = 2025,
        byte gameYear = 25,
        byte playerCarIndex = 3,
        byte secondaryPlayerCarIndex = byte.MaxValue)
    {
        var datagram = new byte[length];
        if (length < 29)
        {
            return datagram;
        }

        WriteUInt16(datagram, 0, packetFormat);
        datagram[2] = gameYear;
        datagram[3] = 1;
        datagram[4] = 7;
        datagram[5] = packetVersion;
        datagram[6] = packetId;
        WriteUInt64(datagram, 7, 0x0123456789ABCDEFUL);
        WriteSingle(datagram, 15, 12.5F);
        WriteUInt32(datagram, 19, 0x10203040U);
        WriteUInt32(datagram, 23, 0x50607080U);
        datagram[27] = playerCarIndex;
        datagram[28] = secondaryPlayerCarIndex;
        return datagram;
    }

    public static void WriteUInt16(byte[] destination, int offset, ushort value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
    }

    public static void WriteUInt32(byte[] destination, int offset, uint value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
        destination[offset + 2] = (byte)(value >> 16);
        destination[offset + 3] = (byte)(value >> 24);
    }

    public static void WriteSingle(byte[] destination, int offset, float value)
    {
        WriteUInt32(
            destination,
            offset,
            unchecked((uint)BitConverter.SingleToInt32Bits(value)));
    }

    public static void WriteAscii4(byte[] destination, int offset, string value)
    {
        if (value.Length != 4 || value.Any(character => character > 0x7F))
        {
            throw new ArgumentException(
                "An exact four-character ASCII value is required.",
                nameof(value));
        }

        for (var index = 0; index < value.Length; index++)
        {
            destination[offset + index] = (byte)value[index];
        }
    }

    private static void WriteUInt64(byte[] destination, int offset, ulong value)
    {
        for (var index = 0; index < sizeof(ulong); index++)
        {
            destination[offset + index] = (byte)(value >> (index * 8));
        }
    }
}
