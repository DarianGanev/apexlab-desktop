using System.Buffers.Binary;

namespace ApexLab.Protocols.F125.Parsing;

internal ref struct LittleEndianSpanReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _position;

    public LittleEndianSpanReader(ReadOnlySpan<byte> source)
    {
        _source = source;
    }

    public readonly int Position => _position;

    public readonly int Remaining => _source.Length - _position;

    public bool TryReadByte(out byte value)
    {
        if (!TryReadBytes(sizeof(byte), out var bytes))
        {
            value = default;
            return false;
        }

        value = bytes[0];
        return true;
    }

    public bool TryReadSByte(out sbyte value)
    {
        if (!TryReadBytes(sizeof(sbyte), out var bytes))
        {
            value = default;
            return false;
        }

        value = unchecked((sbyte)bytes[0]);
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (!TryReadBytes(sizeof(ushort), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        return true;
    }

    public bool TryReadInt16(out short value)
    {
        if (!TryReadBytes(sizeof(short), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadInt16LittleEndian(bytes);
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        if (!TryReadBytes(sizeof(uint), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    public bool TryReadInt32(out int value)
    {
        if (!TryReadBytes(sizeof(int), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    public bool TryReadUInt64(out ulong value)
    {
        if (!TryReadBytes(sizeof(ulong), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    public bool TryReadInt64(out long value)
    {
        if (!TryReadBytes(sizeof(long), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        return true;
    }

    public bool TryReadSingle(out float value)
    {
        if (!TryReadBytes(sizeof(float), out var bytes))
        {
            value = default;
            return false;
        }

        value = BinaryPrimitives.ReadSingleLittleEndian(bytes);
        return true;
    }

    private bool TryReadBytes(int byteCount, out ReadOnlySpan<byte> bytes)
    {
        if (Remaining < byteCount)
        {
            bytes = default;
            return false;
        }

        bytes = _source.Slice(_position, byteCount);
        _position += byteCount;
        return true;
    }
}
