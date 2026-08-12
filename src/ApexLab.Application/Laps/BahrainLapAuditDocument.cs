using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public sealed record BahrainLapAuditDocument
{
    private readonly IReadOnlyList<BahrainLapAuditEntry> _entries;

    public BahrainLapAuditDocument(
        int schemaVersion,
        string lapAuditId,
        string canonicalIdentitySha256,
        string canonicalSha256,
        BahrainTelemetryContext? referenceContext,
        BahrainLapAuditManualInputs manualInputs,
        IEnumerable<BahrainLapAuditEntry> entries)
    {
        if (schemaVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        }

        if (!StringComparer.Ordinal.Equals(
                lapAuditId,
                BahrainLapAuditContract.AuditId))
        {
            throw new ArgumentException(
                "The lap audit version is unsupported.",
                nameof(lapAuditId));
        }

        LapIdentityValidation.RequireSha256(
            canonicalIdentitySha256,
            nameof(canonicalIdentitySha256));
        LapIdentityValidation.RequireSha256(
            canonicalSha256,
            nameof(canonicalSha256));
        ArgumentNullException.ThrowIfNull(manualInputs);
        ArgumentNullException.ThrowIfNull(entries);
        var copied = entries.ToArray();
        if (copied.Length > BahrainLapAuditContract.MaximumInventoryCandidates
            || copied.Any(entry => entry is null))
        {
            throw new ArgumentException(
                "The lap audit entries exceed their supported shape.",
                nameof(entries));
        }

        SchemaVersion = schemaVersion;
        LapAuditId = lapAuditId;
        CanonicalIdentitySha256 = canonicalIdentitySha256;
        CanonicalSha256 = canonicalSha256;
        ReferenceContext = referenceContext;
        ManualInputs = manualInputs;
        _entries = Array.AsReadOnly(copied);
    }

    public int SchemaVersion { get; }
    public string LapAuditId { get; }
    public string CanonicalIdentitySha256 { get; }
    public string CanonicalSha256 { get; }
    public BahrainTelemetryContext? ReferenceContext { get; }
    public BahrainLapAuditManualInputs ManualInputs { get; }
    public IReadOnlyList<BahrainLapAuditEntry> Entries => _entries;

    public static BahrainLapAuditDocument CreateTemplate(
        BahrainLapInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new(
            schemaVersion: 1,
            BahrainLapAuditContract.AuditId,
            inventory.CanonicalIdentitySha256,
            inventory.CanonicalSha256,
            inventory.ReferenceContext,
            BahrainLapAuditManualInputs.Empty(),
            inventory.Candidates.Select(BahrainLapAuditEntry.FromCandidate));
    }
}
