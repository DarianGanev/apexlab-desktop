using System.Net;
using ApexLab.Application.Capture;
using ApexLab.Application.Configuration;
using ApexLab.Application.Storage;
using ApexLab.Persistence.Raw;
using ApexLab.Protocols.F125;
using ApexLab.Telemetry.Udp;

namespace ApexLab.App.Capture;

public sealed class DesktopCaptureSessionFactory(
    ApexLabOptions options) : ICaptureSessionFactory
{
    public async Task<CaptureSessionComponents> CreateAsync(
        RawEvidenceCaptureId captureId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureId);
        if (options.AllowLan
            || !IPAddress.TryParse(
                options.BindAddress,
                out var bindAddress)
            || !IPAddress.IsLoopback(bindAddress))
        {
            throw new InvalidOperationException(
                "Version 0.2 capture requires a loopback bind address.");
        }

        var paths = ApplicationPaths.FromRoot(options.DataRootPath);
        var protocolId = RawEvidenceProtocolId.Parse(F125Protocol.Id);
        RawEvidenceWriter? evidence = null;
        try
        {
            evidence = await RawEvidenceWriter.CreateAsync(
                    paths,
                    captureId,
                    protocolId,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var source = new UdpDatagramSource(
                new UdpDatagramSourceOptions(
                    options.UdpPort));
            return new CaptureSessionComponents(
                source,
                new F125TelemetryProtocolAdapter(),
                SenderPolicy.LoopbackOnly,
                evidence);
        }
        catch
        {
            if (evidence is not null)
            {
                await evidence.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }
}
