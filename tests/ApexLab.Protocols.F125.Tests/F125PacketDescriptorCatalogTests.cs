using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Tests;

[TestClass]
public sealed class F125PacketDescriptorCatalogTests
{
    [TestMethod]
    public void CatalogMatchesIndependentlyAuthoredOfficialPacketTable()
    {
        ExpectedDescriptor[] expected =
        [
            new("Motion", 0, 1, 1349, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Session", 1, 1, 753, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Lap Data", 2, 1, 1285, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Event", 3, 1, 45, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Participants", 4, 1, 1284, TelemetryPacketPrivacyDisposition.IdentityBearingExcluded),
            new("Car Setups", 5, 1, 1133, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Car Telemetry", 6, 1, 1352, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Car Status", 7, 1, 1239, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Final Classification", 8, 1, 1042, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Lobby Info", 9, 1, 954, TelemetryPacketPrivacyDisposition.IdentityBearingExcluded),
            new("Car Damage", 10, 1, 1041, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Session History", 11, 1, 1460, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Tyre Sets", 12, 1, 231, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Motion Ex", 13, 1, 273, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Time Trial", 14, 1, 101, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
            new("Lap Positions", 15, 1, 1131, TelemetryPacketPrivacyDisposition.EvidenceAllowed),
        ];

        Assert.HasCount(expected.Length, F125PacketDescriptorCatalog.Descriptors);

        for (var index = 0; index < expected.Length; index++)
        {
            var expectedDescriptor = expected[index];
            var actual = F125PacketDescriptorCatalog.Descriptors[index];

            Assert.AreEqual(expectedDescriptor.FamilyName, actual.FamilyName);
            Assert.AreEqual(expectedDescriptor.PacketId, actual.PacketId);
            Assert.AreEqual(expectedDescriptor.PacketVersion, actual.PacketVersion);
            Assert.AreEqual(expectedDescriptor.DatagramLength, actual.DatagramLength);
            Assert.AreEqual(expectedDescriptor.PrivacyDisposition, actual.PrivacyDisposition);
            Assert.IsTrue(
                F125PacketDescriptorCatalog.TryGet(expectedDescriptor.PacketId, out var found));
            Assert.AreSame(found, actual);
        }
    }

    [TestMethod]
    public void EveryPacketIdIsUniqueAndUnknownByteIdsAreRejected()
    {
        var ids = F125PacketDescriptorCatalog.Descriptors
            .Select(descriptor => descriptor.PacketId)
            .ToArray();

        Assert.HasCount(ids.Length, ids.Distinct());

        for (var packetId = 16; packetId <= byte.MaxValue; packetId++)
        {
            Assert.IsFalse(
                F125PacketDescriptorCatalog.TryGet((byte)packetId, out var descriptor),
                $"Unexpected descriptor for packet ID {packetId}.");
            Assert.IsNull(descriptor);
        }
    }

    private sealed record ExpectedDescriptor(
        string FamilyName,
        byte PacketId,
        byte PacketVersion,
        int DatagramLength,
        TelemetryPacketPrivacyDisposition PrivacyDisposition);
}
