using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Tests.Laps;

internal static class BahrainLapAuditTestData
{
    public static BahrainLapAuditDocument Document(
        LapAuditDecision? decision = null,
        LapExclusionReason? exclusionReason = null,
        string? factualNote = null)
    {
        var context = Context();
        var boundary = LapBoundary.Complete(10, 20, 7, 90_123);
        var flags = decision == LapAuditDecision.Excluded
            ? LapEvidenceFlags.PitObserved
            : LapEvidenceFlags.None;
        var candidate = new BahrainLapCandidate(
            BahrainLapCandidateIdentity.Calculate(
                new string('a', 64),
                boundary,
                flags,
                context),
            boundary,
            flags,
            context);
        var entry = BahrainLapAuditEntry.FromCandidate(candidate) with
        {
            Decision = decision,
            ExclusionReason = exclusionReason,
            FactualNote = factualNote,
        };
        return new(
            1,
            BahrainLapAuditContract.AuditId,
            new string('a', 64),
            new string('b', 64),
            context,
            decision.HasValue
                ? CompleteInputs()
                : BahrainLapAuditManualInputs.Empty(),
            [entry]);
    }

    public static BahrainTelemetryContext Context() =>
        new(
            new CanonicalSessionPacket(
                weather: 0,
                trackTemperatureCelsius: 30,
                airTemperatureCelsius: 24,
                trackLengthMetres: 5_412,
                sessionType: 18,
                trackId: 3,
                formula: 0,
                isSpectating: false,
                isNetworkGame: false,
                steeringAssist: 0,
                brakingAssist: 0,
                gearboxAssist: 1,
                pitAssist: 0,
                pitReleaseAssist: 0,
                ersAssist: 0,
                drsAssist: 0,
                dynamicRacingLine: 0,
                dynamicRacingLineType: 0,
                gameMode: 5,
                ruleSet: 2,
                timeOfDayMinutesSinceMidnight: 900,
                equalCarPerformance: true,
                recoveryMode: 0),
            playerCarIndex: 7,
            secondaryPlayerCarIndex: byte.MaxValue);

    private static BahrainLapAuditManualInputs CompleteInputs() =>
        new(
            "F1 25 current PC build",
            "F1 car",
            "Wheel profile",
            "Fixed setup A",
            "Soft",
            true,
            true,
            true,
            true);
}
