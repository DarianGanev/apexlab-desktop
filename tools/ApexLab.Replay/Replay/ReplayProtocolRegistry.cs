using System.Diagnostics.CodeAnalysis;
using ApexLab.Protocols.F125;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.Replay.Replay;

internal static class ReplayProtocolRegistry
{
    private static readonly IReadOnlyDictionary<
        string,
        Func<ITelemetryProtocolAdapter>> Factories =
        new Dictionary<
            string,
            Func<ITelemetryProtocolAdapter>>(
            StringComparer.Ordinal)
        {
            [F125Protocol.Id] =
                static () => new F125TelemetryProtocolAdapter(),
        };

    public static bool TryResolve(
        string protocolId,
        [NotNullWhen(true)]
        out ITelemetryProtocolAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(protocolId);
        if (!Factories.TryGetValue(protocolId, out var factory))
        {
            adapter = null;
            return false;
        }

        var resolved = factory();
        if (!string.Equals(
                resolved.ProtocolId,
                protocolId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A replay protocol registry entry reported a different identity.");
        }

        adapter = resolved;
        return true;
    }
}
