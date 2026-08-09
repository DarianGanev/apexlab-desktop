using System.Buffers.Binary;

namespace ApexLab.Protocols.F125.Parsing;

internal static class LittleEndianFieldReader
{
    public static bool TryReadByte(
        ReadOnlySpan<byte> source,
        int offset,
        out byte value)
    {
        if (!TrySlice(source, offset, sizeof(byte), out var bytes))
        {
            value = default;
            return false;
        }

        value = bytes[0];
        return true;
    }

    public static bool TryReadSByte(
        ReadOnlySpan<byte> source,
        int offset,
        out sbyte value)
    {
        if (!TrySlice(source, offset, sizeof(sbyte), out var bytes))
        {
            value = default;
            return false;
        }

        value = unchecked((sbyte)bytes[0]);
        return true;
    }

    public static bool TryReadUInt16(
        ReadOnlySpan<byte> source,
        int offset,
        out ushort value)
    {
        if (!TrySlice(source, offset, sizeof(ushort), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    public static bool TryReadUInt32(
        ReadOnlySpan<byte> source,
        int offset,
        out uint value)
    {
        if (!TrySlice(source, offset, sizeof(uint), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    public static bool TryReadSingle(
        ReadOnlySpan<byte> source,
        int offset,
        out float value)
    {
        if (!TrySlice(source, offset, sizeof(float), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        return true;
    }

    public static bool TryReadAscii4(
        ReadOnlySpan<byte> source,
        int offset,
        out uint value)
    {
        return TryReadUInt32(source, offset, out value);
    }

    private static bool TrySlice(
        ReadOnlySpan<byte> source,
        int offset,
        int width,
        out ReadOnlySpan<byte> slice)
    {
        if (offset < 0 || offset > source.Length - width)
        {
            slice = default;
            return false;
        }

        slice = source.Slice(offset, width);
        return true;
    }
}
