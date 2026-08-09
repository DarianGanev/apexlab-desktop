using ApexLab.Protocols.F125.Parsing;

namespace ApexLab.Protocols.F125.Tests.Parsing;

[TestClass]
public sealed class LittleEndianFieldReaderTests
{
    private static readonly byte[] Golden =
    [
        0xA5,
        0xFE,
        0x34, 0x12,
        0x78, 0x56, 0x34, 0x12,
        0x00, 0x00, 0xC0, 0x3F,
        0x53, 0x53, 0x54, 0x41,
    ];

    [TestMethod]
    public void ReadsEverySelectedPrimitiveAtUnalignedAbsoluteOffsets()
    {
        Assert.IsTrue(LittleEndianFieldReader.TryReadByte(Golden, 0, out var unsignedByte));
        Assert.IsTrue(LittleEndianFieldReader.TryReadSByte(Golden, 1, out var signedByte));
        Assert.IsTrue(LittleEndianFieldReader.TryReadUInt16(Golden, 2, out var unsigned16));
        Assert.IsTrue(LittleEndianFieldReader.TryReadUInt32(Golden, 4, out var unsigned32));
        Assert.IsTrue(LittleEndianFieldReader.TryReadSingle(Golden, 8, out var single));
        Assert.IsTrue(LittleEndianFieldReader.TryReadAscii4(Golden, 12, out var ascii4));

        Assert.AreEqual((byte)0xA5, unsignedByte);
        Assert.AreEqual((sbyte)-2, signedByte);
        Assert.AreEqual((ushort)0x1234, unsigned16);
        Assert.AreEqual(0x12345678U, unsigned32);
        Assert.AreEqual(1.5F, single);
        Assert.AreEqual(0x41545353U, ascii4);
    }

    [TestMethod]
    public void EveryInvalidOffsetOrWidthFailsWithoutPublishingOutput()
    {
        int[] invalidOffsets = [-1, Golden.Length, Golden.Length + 1, int.MaxValue];

        foreach (var offset in invalidOffsets)
        {
            Assert.IsFalse(LittleEndianFieldReader.TryReadByte(Golden, offset, out var byteValue));
            Assert.AreEqual(default, byteValue);
            Assert.IsFalse(LittleEndianFieldReader.TryReadSByte(Golden, offset, out var sbyteValue));
            Assert.AreEqual(default, sbyteValue);
            Assert.IsFalse(LittleEndianFieldReader.TryReadUInt16(Golden, offset, out var uint16Value));
            Assert.AreEqual(default, uint16Value);
            Assert.IsFalse(LittleEndianFieldReader.TryReadUInt32(Golden, offset, out var uint32Value));
            Assert.AreEqual(default, uint32Value);
            Assert.IsFalse(LittleEndianFieldReader.TryReadSingle(Golden, offset, out var singleValue));
            Assert.AreEqual(default, singleValue);
            Assert.IsFalse(LittleEndianFieldReader.TryReadAscii4(Golden, offset, out var ascii4Value));
            Assert.AreEqual(default, ascii4Value);
        }

        for (var remaining = 0; remaining < sizeof(uint); remaining++)
        {
            var offset = Golden.Length - remaining;
            Assert.IsFalse(LittleEndianFieldReader.TryReadUInt32(Golden, offset, out var uint32Value));
            Assert.AreEqual(default, uint32Value);
            Assert.IsFalse(LittleEndianFieldReader.TryReadSingle(Golden, offset, out var singleValue));
            Assert.AreEqual(default, singleValue);
            Assert.IsFalse(LittleEndianFieldReader.TryReadAscii4(Golden, offset, out var ascii4Value));
            Assert.AreEqual(default, ascii4Value);
        }
    }

    [TestMethod]
    public void ExactLastFieldWidthsRemainReadable()
    {
        Assert.IsTrue(LittleEndianFieldReader.TryReadByte(Golden, Golden.Length - 1, out var last));
        Assert.AreEqual((byte)0x41, last);
        Assert.IsTrue(LittleEndianFieldReader.TryReadUInt16(Golden, Golden.Length - 2, out var lastTwo));
        Assert.AreEqual((ushort)0x4154, lastTwo);
        Assert.IsTrue(LittleEndianFieldReader.TryReadAscii4(Golden, Golden.Length - 4, out var lastFour));
        Assert.AreEqual(0x41545353U, lastFour);
    }
}
