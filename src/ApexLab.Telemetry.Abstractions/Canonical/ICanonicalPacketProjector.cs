namespace ApexLab.Telemetry.Abstractions.Canonical;

public interface ICanonicalPacketProjector
{
    string ProtocolId { get; }

    string ContractId { get; }

    string DecoderId { get; }

    string CanonicalSchemaId { get; }

    CanonicalProjectionResult Project(ReadOnlySpan<byte> datagram);
}
