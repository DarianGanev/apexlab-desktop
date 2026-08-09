namespace ApexLab.Telemetry.Abstractions.Protocol;

public readonly record struct TelemetryPacketResult
{
    private TelemetryPacketResult(
        TelemetryPacketClassification classification,
        TelemetryHeaderMetadata? header,
        TelemetryPacketDescriptor? descriptor)
    {
        Classification = classification;
        Header = header;
        Descriptor = descriptor;
    }

    public TelemetryPacketClassification Classification { get; }

    public TelemetryHeaderMetadata? Header { get; }

    public TelemetryPacketDescriptor? Descriptor { get; }

    public bool IsCompatible =>
        Classification == TelemetryPacketClassification.Compatible
        && Header.HasValue
        && Descriptor is not null;

    public static TelemetryPacketResult Compatible(
        TelemetryHeaderMetadata header,
        TelemetryPacketDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.PrivacyDisposition
            != TelemetryPacketPrivacyDisposition.EvidenceAllowed)
        {
            throw new ArgumentException(
                "A compatible packet must be explicitly allowed for evidence retention.",
                nameof(descriptor));
        }

        if (descriptor.PacketId != header.PacketId
            || descriptor.PacketVersion != header.PacketVersion)
        {
            throw new ArgumentException(
                "A compatible packet descriptor must match the decoded packet ID and version.",
                nameof(descriptor));
        }

        return new(TelemetryPacketClassification.Compatible, header, descriptor);
    }

    public static TelemetryPacketResult Rejected(
        TelemetryPacketClassification classification,
        TelemetryHeaderMetadata? header = null,
        TelemetryPacketDescriptor? descriptor = null)
    {
        if (classification is TelemetryPacketClassification.Unspecified
            or TelemetryPacketClassification.Compatible
            || !Enum.IsDefined(classification))
        {
            throw new ArgumentOutOfRangeException(
                nameof(classification),
                "A known non-compatible classification is required.");
        }

        return new(classification, header, descriptor);
    }
}
