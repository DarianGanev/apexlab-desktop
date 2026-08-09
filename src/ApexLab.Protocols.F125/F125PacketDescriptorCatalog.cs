using System.Diagnostics.CodeAnalysis;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125;

public static class F125PacketDescriptorCatalog
{
    private static readonly IReadOnlyList<TelemetryPacketDescriptor> DescriptorTable =
        Array.AsReadOnly<TelemetryPacketDescriptor>(
        [
            Allowed("Motion", 0, 1349),
            Allowed("Session", 1, 753),
            Allowed("Lap Data", 2, 1285),
            Allowed("Event", 3, 45),
            Excluded("Participants", 4, 1284),
            Allowed("Car Setups", 5, 1133),
            Allowed("Car Telemetry", 6, 1352),
            Allowed("Car Status", 7, 1239),
            Allowed("Final Classification", 8, 1042),
            Excluded("Lobby Info", 9, 954),
            Allowed("Car Damage", 10, 1041),
            Allowed("Session History", 11, 1460),
            Allowed("Tyre Sets", 12, 231),
            Allowed("Motion Ex", 13, 273),
            Allowed("Time Trial", 14, 101),
            Allowed("Lap Positions", 15, 1131),
        ]);

    private static readonly IReadOnlyDictionary<byte, TelemetryPacketDescriptor> DescriptorLookup =
        DescriptorTable.ToDictionary(descriptor => descriptor.PacketId);

    public static IReadOnlyList<TelemetryPacketDescriptor> Descriptors => DescriptorTable;

    public static bool TryGet(
        byte packetId,
        [NotNullWhen(true)] out TelemetryPacketDescriptor? descriptor)
    {
        return DescriptorLookup.TryGetValue(packetId, out descriptor);
    }

    private static TelemetryPacketDescriptor Allowed(
        string familyName,
        byte packetId,
        int datagramLength)
    {
        return new(
            familyName,
            packetId,
            packetVersion: 1,
            datagramLength,
            TelemetryPacketPrivacyDisposition.EvidenceAllowed);
    }

    private static TelemetryPacketDescriptor Excluded(
        string familyName,
        byte packetId,
        int datagramLength)
    {
        return new(
            familyName,
            packetId,
            packetVersion: 1,
            datagramLength,
            TelemetryPacketPrivacyDisposition.IdentityBearingExcluded);
    }
}
