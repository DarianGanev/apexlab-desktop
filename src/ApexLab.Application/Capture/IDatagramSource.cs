using System.Threading.Channels;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Capture;

public interface IDatagramSource : IAsyncDisposable
{
    ChannelReader<DatagramEnvelope> Output { get; }

    DatagramSourceCounters Counters { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
