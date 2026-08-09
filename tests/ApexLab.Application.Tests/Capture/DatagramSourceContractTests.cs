using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class DatagramSourceContractTests
{
    [TestMethod]
    public async Task SourceContract_ExposesOwnedOutputCountersAndLifecycleOperations()
    {
        var stub = new StubSource();
        IDatagramSource source = stub;

        await source.StartAsync(TestContext.CancellationToken);
        await source.StartAsync(TestContext.CancellationToken);
        await source.StopAsync(TestContext.CancellationToken);
        await source.StopAsync(TestContext.CancellationToken);
        await source.DisposeAsync();

        Assert.AreSame(stub.Output, source.Output);
        Assert.AreEqual(default, source.Counters);
        Assert.AreEqual(2, stub.StartCalls);
        Assert.AreEqual(2, stub.StopCalls);
        Assert.AreEqual(1, stub.DisposeCalls);
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class StubSource : IDatagramSource
    {
        private readonly Channel<DatagramEnvelope> _channel =
            Channel.CreateUnbounded<DatagramEnvelope>();

        public ChannelReader<DatagramEnvelope> Output => _channel.Reader;

        public DatagramSourceCounters Counters => default;

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}
