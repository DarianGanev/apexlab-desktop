using ApexLab.Protocols.F125.Parsing;

namespace ApexLab.Protocols.F125.Tests.Parsing;

[TestClass]
public sealed class LittleEndianSpanReaderTests
{
    [TestMethod]
    public void ReadsEverySupportedPrimitiveInLittleEndianOrder()
    {
        ReadOnlySpan<byte> source =
        [
            0xFE,
            0xFE,
            0x34, 0x12,
            0xFE, 0xFF,
            0x78, 0x56, 0x34, 0x12,
            0xFE, 0xFF, 0xFF, 0xFF,
            0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01,
            0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0xC0, 0x3F,
        ];
        var reader = new LittleEndianSpanReader(source);

        Assert.IsTrue(reader.TryReadByte(out var unsignedByte));
        Assert.IsTrue(reader.TryReadSByte(out var signedByte));
        Assert.IsTrue(reader.TryReadUInt16(out var unsigned16));
        Assert.IsTrue(reader.TryReadInt16(out var signed16));
        Assert.IsTrue(reader.TryReadUInt32(out var unsigned32));
        Assert.IsTrue(reader.TryReadInt32(out var signed32));
        Assert.IsTrue(reader.TryReadUInt64(out var unsigned64));
        Assert.IsTrue(reader.TryReadInt64(out var signed64));
        Assert.IsTrue(reader.TryReadSingle(out var single));

        Assert.AreEqual(byte.MaxValue - 1, unsignedByte);
        Assert.AreEqual((sbyte)-2, signedByte);
        Assert.AreEqual((ushort)0x1234, unsigned16);
        Assert.AreEqual((short)-2, signed16);
        Assert.AreEqual(0x12345678U, unsigned32);
        Assert.AreEqual(-2, signed32);
        Assert.AreEqual(0x0123456789ABCDEFUL, unsigned64);
        Assert.AreEqual(-2L, signed64);
        Assert.AreEqual(1.5F, single);
        Assert.AreEqual(source.Length, reader.Position);
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void ReadsMultiByteValuesFromUnalignedOffsets()
    {
        ReadOnlySpan<byte> source = [0xAA, 0x34, 0x12, 0x78, 0x56, 0x34, 0x12];
        var reader = new LittleEndianSpanReader(source);

        Assert.IsTrue(reader.TryReadByte(out _));
        Assert.IsTrue(reader.TryReadUInt16(out var unsigned16));
        Assert.IsTrue(reader.TryReadUInt32(out var unsigned32));

        Assert.AreEqual((ushort)0x1234, unsigned16);
        Assert.AreEqual(0x12345678U, unsigned32);
        Assert.AreEqual(source.Length, reader.Position);
    }

    [TestMethod]
    public void EmptyInputFailsEveryReadWithoutAdvancingOrPublishingOutput()
    {
        var reader = new LittleEndianSpanReader(ReadOnlySpan<byte>.Empty);

        Assert.IsFalse(reader.TryReadByte(out var unsignedByte));
        Assert.AreEqual(default, unsignedByte);
        Assert.IsFalse(reader.TryReadSByte(out var signedByte));
        Assert.AreEqual(default, signedByte);
        Assert.IsFalse(reader.TryReadUInt16(out var unsigned16));
        Assert.AreEqual(default, unsigned16);
        Assert.IsFalse(reader.TryReadInt16(out var signed16));
        Assert.AreEqual(default, signed16);
        Assert.IsFalse(reader.TryReadUInt32(out var unsigned32));
        Assert.AreEqual(default, unsigned32);
        Assert.IsFalse(reader.TryReadInt32(out var signed32));
        Assert.AreEqual(default, signed32);
        Assert.IsFalse(reader.TryReadUInt64(out var unsigned64));
        Assert.AreEqual(default, unsigned64);
        Assert.IsFalse(reader.TryReadInt64(out var signed64));
        Assert.AreEqual(default, signed64);
        Assert.IsFalse(reader.TryReadSingle(out var single));
        Assert.AreEqual(default, single);
        Assert.AreEqual(0, reader.Position);
        Assert.AreEqual(0, reader.Remaining);
    }

    [TestMethod]
    public void OneByteShortReadsFailWithoutAdvancing()
    {
        ReadOnlySpan<byte> oneByte = [0x01];
        ReadOnlySpan<byte> threeBytes = [0x01, 0x02, 0x03];
        ReadOnlySpan<byte> sevenBytes = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        var unsigned16Reader = new LittleEndianSpanReader(oneByte);
        var signed16Reader = new LittleEndianSpanReader(oneByte);
        var unsigned32Reader = new LittleEndianSpanReader(threeBytes);
        var signed32Reader = new LittleEndianSpanReader(threeBytes);
        var unsigned64Reader = new LittleEndianSpanReader(sevenBytes);
        var signed64Reader = new LittleEndianSpanReader(sevenBytes);
        var singleReader = new LittleEndianSpanReader(threeBytes);

        Assert.IsFalse(unsigned16Reader.TryReadUInt16(out _));
        Assert.IsFalse(signed16Reader.TryReadInt16(out _));
        Assert.IsFalse(unsigned32Reader.TryReadUInt32(out _));
        Assert.IsFalse(signed32Reader.TryReadInt32(out _));
        Assert.IsFalse(unsigned64Reader.TryReadUInt64(out _));
        Assert.IsFalse(signed64Reader.TryReadInt64(out _));
        Assert.IsFalse(singleReader.TryReadSingle(out _));
        Assert.AreEqual(0, unsigned16Reader.Position);
        Assert.AreEqual(0, signed16Reader.Position);
        Assert.AreEqual(0, unsigned32Reader.Position);
        Assert.AreEqual(0, signed32Reader.Position);
        Assert.AreEqual(0, unsigned64Reader.Position);
        Assert.AreEqual(0, signed64Reader.Position);
        Assert.AreEqual(0, singleReader.Position);
    }

    [TestMethod]
    public void EveryInsufficientLengthFailsWithoutAdvancingOrPublishingOutput()
    {
        for (var length = 0; length < sizeof(uint); length++)
        {
            var source = new byte[length];
            source.AsSpan().Fill(0xA5);

            var unsignedReader = new LittleEndianSpanReader(source);
            var signedReader = new LittleEndianSpanReader(source);
            var singleReader = new LittleEndianSpanReader(source);

            Assert.IsFalse(unsignedReader.TryReadUInt32(out var unsignedValue));
            Assert.AreEqual(default, unsignedValue);
            Assert.AreEqual(0, unsignedReader.Position);
            Assert.AreEqual(length, unsignedReader.Remaining);

            Assert.IsFalse(signedReader.TryReadInt32(out var signedValue));
            Assert.AreEqual(default, signedValue);
            Assert.AreEqual(0, signedReader.Position);
            Assert.AreEqual(length, signedReader.Remaining);

            Assert.IsFalse(singleReader.TryReadSingle(out var singleValue));
            Assert.AreEqual(default, singleValue);
            Assert.AreEqual(0, singleReader.Position);
            Assert.AreEqual(length, singleReader.Remaining);
        }

        for (var length = 0; length < sizeof(ulong); length++)
        {
            var source = new byte[length];
            source.AsSpan().Fill(0xA5);

            var unsignedReader = new LittleEndianSpanReader(source);
            var signedReader = new LittleEndianSpanReader(source);

            Assert.IsFalse(unsignedReader.TryReadUInt64(out var unsignedValue));
            Assert.AreEqual(default, unsignedValue);
            Assert.AreEqual(0, unsignedReader.Position);
            Assert.AreEqual(length, unsignedReader.Remaining);

            Assert.IsFalse(signedReader.TryReadInt64(out var signedValue));
            Assert.AreEqual(default, signedValue);
            Assert.AreEqual(0, signedReader.Position);
            Assert.AreEqual(length, signedReader.Remaining);
        }
    }

    [TestMethod]
    public void FailedReadLeavesBytesAvailableForAValidSmallerRead()
    {
        ReadOnlySpan<byte> source = [0x34, 0x12];
        var reader = new LittleEndianSpanReader(source);

        Assert.IsFalse(reader.TryReadUInt32(out _));
        Assert.AreEqual(0, reader.Position);
        Assert.AreEqual(2, reader.Remaining);
        Assert.IsTrue(reader.TryReadUInt16(out var recovered));

        Assert.AreEqual((ushort)0x1234, recovered);
        Assert.AreEqual(2, reader.Position);
    }

    [TestMethod]
    public void SinglePreservesIeee754Bits()
    {
        int[] expectedBits =
        [
            0,
            int.MinValue,
            0x3FC00000,
            0x7F800000,
            unchecked((int)0xFF800000),
            0x7FC01234,
        ];

        foreach (var bits in expectedBits)
        {
            var source = BitConverter.GetBytes(bits);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(source);
            }

            var reader = new LittleEndianSpanReader(source);

            Assert.IsTrue(reader.TryReadSingle(out var value));
            Assert.AreEqual(bits, BitConverter.SingleToInt32Bits(value));
            Assert.AreEqual(sizeof(float), reader.Position);
        }
    }

    [TestMethod]
    public void FixedSeedCorpusMatchesIndependentlyCalculatedUInt64Values()
    {
        const int seed = 0x1252025;
        var random = new Random(seed);
        var source = new byte[sizeof(ulong)];

        for (var sample = 0; sample < 512; sample++)
        {
            random.NextBytes(source);
            var expected = 0UL;
            for (var byteIndex = 0; byteIndex < source.Length; byteIndex++)
            {
                expected |= (ulong)source[byteIndex] << (byteIndex * 8);
            }

            var reader = new LittleEndianSpanReader(source);

            Assert.IsTrue(
                reader.TryReadUInt64(out var actual),
                $"Seed {seed}, sample {sample} did not decode.");
            Assert.AreEqual(
                expected,
                actual,
                $"Seed {seed}, sample {sample} decoded differently.");
        }
    }
}
