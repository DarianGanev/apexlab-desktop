namespace ApexLab.Telemetry.Abstractions.Protocol;

public interface ITelemetryProtocolAdapter
{
    string ProtocolId { get; }

    int HeaderLength { get; }

    TelemetryPacketResult Inspect(ReadOnlySpan<byte> datagram);
}
