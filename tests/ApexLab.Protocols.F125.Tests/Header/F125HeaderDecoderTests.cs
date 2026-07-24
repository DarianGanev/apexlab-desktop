using ApexLab.Protocols.F125.Header;

namespace ApexLab.Protocols.F125.Tests.Header;

[TestClass]
public sealed class F125HeaderDecoderTests
{
    private static readonly byte[] GoldenHeader =
    [
        0xE9, 0x07,
        0x19,
        0x01,
        0x07,
        0x03,
        0x06,
        0xEF, 0xCD, 0xAB, 0x89, 0x67, 0x45, 0x23, 0x01,
        0x00, 0x00, 0x48, 0x41,
        0x40, 0x30, 0x20, 0x10,
        0x80, 0x70, 0x60, 0x50,
        0x03,
        0xFF,
    ];

    [TestMethod]
    public void DecodesIndependentlyAuthoredGoldenHeader()
    {
        var decoded = F125HeaderDecoder.TryDecode(GoldenHeader, out var header);

        Assert.IsTrue(decoded);
        Assert.AreEqual((ushort)2025, header.PacketFormat);
        Assert.AreEqual((byte)25, header.GameYear);
        Assert.AreEqual((byte)1, header.GameMajorVersion);
        Assert.AreEqual((byte)7, header.GameMinorVersion);
        Assert.AreEqual((byte)3, header.PacketVersion);
        Assert.AreEqual((byte)6, header.PacketId);
        Assert.AreEqual(0x0123456789ABCDEFUL, header.SessionUid);
        Assert.AreEqual(12.5F, header.SessionTimeSeconds);
        Assert.AreEqual(0x10203040U, header.FrameIdentifier);
        Assert.AreEqual(0x50607080U, header.OverallFrameIdentifier);
        Assert.AreEqual((byte)3, header.PlayerCarIndex);
        Assert.AreEqual(byte.MaxValue, header.SecondaryPlayerCarIndex);
    }

    [TestMethod]
    public void EveryTruncatedPrefixFailsWithoutPublishingPartialHeader()
    {
        for (var length = 0; length < F125Protocol.HeaderLength; length++)
        {
            var decoded = F125HeaderDecoder.TryDecode(GoldenHeader.AsSpan(0, length), out var header);

            Assert.IsFalse(decoded, $"Prefix length {length} unexpectedly decoded.");
            Assert.AreEqual(default, header, $"Prefix length {length} published partial data.");
        }
    }

    [TestMethod]
    public void TrailingPacketBodyDoesNotChangeHeaderDecode()
    {
        var datagram = new byte[GoldenHeader.Length + 17];
        GoldenHeader.CopyTo(datagram, 0);
        datagram.AsSpan(GoldenHeader.Length).Fill(0xA5);

        var decoded = F125HeaderDecoder.TryDecode(datagram, out var header);

        Assert.IsTrue(decoded);
        Assert.AreEqual(0x0123456789ABCDEFUL, header.SessionUid);
        Assert.AreEqual((byte)6, header.PacketId);
    }

    [TestMethod]
    public void StructuralDecodePreservesUnsupportedFormatYearAndPacketId()
    {
        var datagram = GoldenHeader.ToArray();
        datagram[0] = 0xEA;
        datagram[1] = 0x07;
        datagram[2] = 26;
        datagram[6] = byte.MaxValue;

        var decoded = F125HeaderDecoder.TryDecode(datagram, out var header);

        Assert.IsTrue(decoded);
        Assert.AreEqual((ushort)2026, header.PacketFormat);
        Assert.AreEqual((byte)26, header.GameYear);
        Assert.AreEqual(byte.MaxValue, header.PacketId);
    }

    [TestMethod]
    public void NonFiniteSessionTimeFailsWithoutPublishingHeader()
    {
        int[] invalidBits =
        [
            0x7F800000,
            unchecked((int)0xFF800000),
            0x7FC01234,
        ];

        foreach (var bits in invalidBits)
        {
            var datagram = GoldenHeader.ToArray();
            WriteLittleEndianInt32(datagram.AsSpan(15, sizeof(float)), bits);

            var decoded = F125HeaderDecoder.TryDecode(datagram, out var header);

            Assert.IsFalse(decoded, $"Session-time bits 0x{bits:X8} unexpectedly decoded.");
            Assert.AreEqual(default, header);
        }
    }

    [TestMethod]
    public void NegativeFiniteSessionTimeIsStructurallyPreserved()
    {
        var datagram = GoldenHeader.ToArray();
        WriteLittleEndianInt32(
            datagram.AsSpan(15, sizeof(float)),
            BitConverter.SingleToInt32Bits(-1F));

        var decoded = F125HeaderDecoder.TryDecode(datagram, out var header);

        Assert.IsTrue(decoded);
        Assert.AreEqual(-1F, header.SessionTimeSeconds);
    }

    [TestMethod]
    public void FixedSeedRandomCorpusNeverThrowsOrPublishesPartialHeader()
    {
        const int seed = 0xF125;
        var random = new Random(seed);

        for (var sample = 0; sample < 2_048; sample++)
        {
            var datagram = new byte[random.Next(0, 2_050)];
            random.NextBytes(datagram);

            var decoded = F125HeaderDecoder.TryDecode(datagram, out var header);

            if (!decoded)
            {
                Assert.AreEqual(
                    default,
                    header,
                    $"Seed {seed}, sample {sample} published partial data.");
            }
        }
    }

    private static void WriteLittleEndianInt32(Span<byte> destination, int value)
    {
        destination[0] = (byte)value;
        destination[1] = (byte)(value >> 8);
        destination[2] = (byte)(value >> 16);
        destination[3] = (byte)(value >> 24);
    }
}
