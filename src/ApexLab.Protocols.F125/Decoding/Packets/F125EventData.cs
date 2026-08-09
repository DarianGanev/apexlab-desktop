using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Protocols.F125.Decoding;

public readonly record struct F125EventData
{
    internal F125EventData(
        TelemetryHeaderMetadata header,
        F125SliceEventKind kind,
        uint? flashbackFrameIdentifier = null,
        float? flashbackSessionTimeSeconds = null)
    {
        Header = header;
        Kind = kind;
        FlashbackFrameIdentifier = flashbackFrameIdentifier;
        FlashbackSessionTimeSeconds = flashbackSessionTimeSeconds;
    }

    public TelemetryHeaderMetadata Header { get; }

    public F125SliceEventKind Kind { get; }

    public uint? FlashbackFrameIdentifier { get; }

    public float? FlashbackSessionTimeSeconds { get; }
}
