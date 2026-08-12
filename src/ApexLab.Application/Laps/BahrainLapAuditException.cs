namespace ApexLab.Application.Laps;

public enum BahrainLapAuditFailureKind
{
    ProvenanceMismatch = 1,
    InventoryMismatch = 2,
    InvalidDecision = 3,
    InvalidManualContext = 4,
}

public sealed class BahrainLapAuditException : Exception
{
    public BahrainLapAuditException(
        BahrainLapAuditFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public BahrainLapAuditFailureKind Kind { get; }
}
