using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using ApexLab.Application.Capture;
using ApexLab.Replay.Probe;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.IntegrationTests.Probe;

[TestClass]
public sealed class ProbeArgumentsTests
{
    [TestMethod]
    public void ParsesExplicitProbeOptions()
    {
        string[] arguments =
        [
            "probe",
            "--port", "20778",
            "--capacity", "64",
            "--max-datagram-bytes", "2048",
            "--duration-seconds", "15",
        ];

        var parsed = ProbeArguments.TryParse(arguments, out var result);

        Assert.IsTrue(parsed);
        Assert.IsNotNull(result);
        Assert.AreEqual(20_778, result.Port);
        Assert.AreEqual(64, result.ChannelCapacity);
        Assert.AreEqual(2_048, result.MaximumDatagramBytes);
        Assert.AreEqual(TimeSpan.FromSeconds(15), result.Duration);
    }

    [TestMethod]
    public void RejectsUnknownDuplicateMissingAndOutOfRangeOptions()
    {
        string[][] invalidArguments =
        [
            [],
            ["capture"],
            ["probe", "--unknown", "1"],
            ["probe", "--port"],
            ["probe", "--port", "0"],
            ["probe", "--duration-seconds", "3601"],
            ["probe", "--capacity", "1", "--capacity", "2"],
            ["probe", "--max-datagram-bytes", "28"],
            ["probe", "--duration-seconds", "+1"],
        ];

        foreach (var arguments in invalidArguments)
        {
            Assert.IsFalse(
                ProbeArguments.TryParse(arguments, out var parsed),
                string.Join(' ', arguments));
            Assert.IsNull(parsed);
        }
    }

    [TestMethod]
    public async Task InvalidCommandReturnsMachineReadableExitCodeWithoutStartingCapture()
    {
        var result = await ProbeCommand.ExecuteAsync(
            ["not-probe"],
            TestContext.CancellationToken);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.InvalidArguments, result.ExitCode);
        Assert.AreEqual(
            "invalidArguments",
            document.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task PortConflictReturnsBindFailureWithoutSocketDetails()
    {
        using var occupied = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
        };
        occupied.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)occupied.LocalEndPoint!).Port;

        var result = await ProbeCommand.ExecuteAsync(
            ["probe", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            TestContext.CancellationToken);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.BindFailure, result.ExitCode);
        CollectionAssert.AreEquivalent(
            new[] { "schemaVersion", "status" },
            document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.AreEqual(
            "bindFailure",
            document.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain(port.ToString(), result.Json, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task PreCanceledProbeReturnsInterruptedAggregateReport()
    {
        using var interruption = new CancellationTokenSource();
        interruption.Cancel();

        var result = await ProbeCommand.ExecuteAsync(
            ["probe"],
            interruption.Token);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.Interrupted, result.ExitCode);
        Assert.AreEqual(
            "interrupted",
            document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            0L,
            document.RootElement
                .GetProperty("classification")
                .GetProperty("sourceDequeued")
                .GetInt64());
    }

    [TestMethod]
    public async Task TimedShutdownObservesRunFailureEvenWhenStopFailsFirst()
    {
        var stopFailure = new InvalidOperationException("stop failed");
        var runFailure = new IOException("run failed");
        var runCompletion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var shutdown = ProbeCommand.StopAndObserveRunAsync(
            () => Task.FromException(stopFailure),
            runCompletion.Task);

        Assert.IsFalse(shutdown.IsCompleted);
        runCompletion.SetException(runFailure);

        var combined = await Assert.ThrowsExactlyAsync<AggregateException>(
            () => shutdown);
        CollectionAssert.AreEquivalent(
            new Exception[] { stopFailure, runFailure },
            combined.InnerExceptions.ToArray());
    }

    [TestMethod]
    public async Task TimedStopFailureWithOpenOutputReturnsUnexpectedFailure()
    {
        var source = new StopFailureOpenSource();

        var result = await ProbeCommand.ExecuteAsync(
                ["probe", "--duration-seconds", "1"],
                TestContext.CancellationToken,
                _ => source)
            .WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.CancellationToken);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.UnexpectedFailure, result.ExitCode);
        Assert.AreEqual(
            "unexpectedFailure",
            document.RootElement.GetProperty("status").GetString());
        Assert.IsGreaterThanOrEqualTo(2, source.StopCalls);
        Assert.AreEqual(1, source.DisposeCalls);
    }

    [TestMethod]
    public async Task DisposalFailureReturnsUnexpectedFailureContract()
    {
        var result = await ProbeCommand.ExecuteAsync(
            ["probe"],
            TestContext.CancellationToken,
            _ => new DisposalFailureSource(
                new IOException("synthetic disposal failure")));
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.UnexpectedFailure, result.ExitCode);
        CollectionAssert.AreEquivalent(
            new[] { "schemaVersion", "status" },
            document.RootElement
                .EnumerateObject()
                .Select(property => property.Name)
                .ToArray());
        Assert.AreEqual(
            "unexpectedFailure",
            document.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task SocketFailureDuringDisposalIsNotReportedAsBindFailure()
    {
        var result = await ProbeCommand.ExecuteAsync(
            ["probe"],
            TestContext.CancellationToken,
            _ => new DisposalFailureSource(
                new SocketException((int)SocketError.ConnectionReset)));
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.UnexpectedFailure, result.ExitCode);
        Assert.AreEqual(
            "unexpectedFailure",
            document.RootElement.GetProperty("status").GetString());
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class DisposalFailureSource : IDatagramSource
    {
        private readonly Channel<DatagramEnvelope> _output =
            Channel.CreateUnbounded<DatagramEnvelope>();
        private readonly Exception _disposalFailure;

        public DisposalFailureSource(Exception disposalFailure)
        {
            _disposalFailure = disposalFailure;
            _output.Writer.TryComplete();
        }

        public ChannelReader<DatagramEnvelope> Output => _output.Reader;

        public DatagramSourceCounters Counters => default;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.FromException(_disposalFailure);
        }
    }

    private sealed class StopFailureOpenSource : IDatagramSource
    {
        private readonly Channel<DatagramEnvelope> _output =
            Channel.CreateUnbounded<DatagramEnvelope>();
        private readonly IOException _stopFailure =
            new("synthetic stop failure");
        private int _stopCalls;
        private int _disposeCalls;

        public ChannelReader<DatagramEnvelope> Output => _output.Reader;

        public DatagramSourceCounters Counters => default;

        public int StopCalls => Volatile.Read(ref _stopCalls);

        public int DisposeCalls => Volatile.Read(ref _disposeCalls);

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _stopCalls);
            return Task.FromException(_stopFailure);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            _output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
