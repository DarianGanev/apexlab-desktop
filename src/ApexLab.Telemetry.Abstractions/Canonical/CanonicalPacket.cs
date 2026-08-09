namespace ApexLab.Telemetry.Abstractions.Canonical;

public readonly record struct CanonicalPacket
{
    private CanonicalPacket(
        CanonicalPacketFamily family,
        CanonicalPacketHeader header,
        CanonicalMotionPacket? motion = null,
        CanonicalSessionPacket? session = null,
        CanonicalLapPacket? lap = null,
        CanonicalEventPacket? @event = null,
        CanonicalCarTelemetryPacket? carTelemetry = null)
    {
        Family = family;
        Header = header;
        Motion = motion;
        Session = session;
        Lap = lap;
        Event = @event;
        CarTelemetry = carTelemetry;
    }

    public CanonicalPacketFamily Family { get; }
    public CanonicalPacketHeader Header { get; }
    public CanonicalMotionPacket? Motion { get; }
    public CanonicalSessionPacket? Session { get; }
    public CanonicalLapPacket? Lap { get; }
    public CanonicalEventPacket? Event { get; }
    public CanonicalCarTelemetryPacket? CarTelemetry { get; }

    public static CanonicalPacket CreateMotion(
        CanonicalPacketHeader header,
        CanonicalMotionPacket value) =>
        new(CanonicalPacketFamily.Motion, header, motion: value);

    public static CanonicalPacket CreateSession(
        CanonicalPacketHeader header,
        CanonicalSessionPacket value) =>
        new(CanonicalPacketFamily.Session, header, session: value);

    public static CanonicalPacket CreateLapData(
        CanonicalPacketHeader header,
        CanonicalLapPacket value) =>
        new(CanonicalPacketFamily.LapData, header, lap: value);

    public static CanonicalPacket CreateEvent(
        CanonicalPacketHeader header,
        CanonicalEventPacket value)
    {
        if (value.Kind == CanonicalEventKind.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return new(CanonicalPacketFamily.Event, header, @event: value);
    }

    public static CanonicalPacket CreateCarTelemetry(
        CanonicalPacketHeader header,
        CanonicalCarTelemetryPacket value) =>
        new(
            CanonicalPacketFamily.CarTelemetry,
            header,
            carTelemetry: value);
}
