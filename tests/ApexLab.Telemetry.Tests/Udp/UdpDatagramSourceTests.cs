using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;
using ApexLab.Telemetry.Udp;

namespace ApexLab.Telemetry.Tests.Udp;

[TestClass]
public sealed class UdpDatagramSourceTests
{
    [TestMethod]
    public async Task Source_ReceivesAnExactOwnedLoopbackDatagram()
    {
        await using var source = CreateSource();
        await source.StartAsync();
        var endpoint = GetBoundEndpoint(source);
        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40 };

        await SendAsync(endpoint, payload);
        payload.AsSpan().Fill(0xFF);
        var envelope = await source.Output.ReadAsync().AsTask().WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(1L, envelope.Sequence);
        Assert.IsGreaterThanOrEqualTo(0L, envelope.MonotonicTimestamp);
        Assert.AreEqual(TimeSpan.Zero, envelope.ReceivedAtUtc.Offset);
        Assert.IsNotNull(envelope.Sender.Address);
        Assert.IsTrue(IPAddress.IsLoopback(envelope.Sender.Address));
        Assert.IsGreaterThan(0, envelope.Sender.Port);
        CollectionAssert.AreEqual(
            new byte[] { 0x10, 0x20, 0x30, 0x40 },
            envelope.Payload.ToArray());

        await source.StopAsync();
        Assert.AreEqual(
            new DatagramSourceCounters(1, 1, 0, 0, 0),
            source.Counters);
        await source.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task Source_AcceptsTheConfiguredBoundaryAndRejectsTheSentinelLength()
    {
        await using var source = CreateSource(
            channelCapacity: 4,
            maximumDatagramBytes: 4);
        await source.StartAsync();
        var endpoint = GetBoundEndpoint(source);

        await SendAsync(endpoint, [1, 2, 3, 4]);
        await SendAsync(endpoint, new byte[32]);
        await SendAsync(endpoint, [9, 8, 7]);
        await WaitUntilAsync(() => source.Counters.DatagramsObserved == 3);
        await source.StopAsync();

        var boundary = await source.Output.ReadAsync();
        var afterOversized = await source.Output.ReadAsync();
        CollectionAssert.AreEqual(
            new byte[] { 1, 2, 3, 4 },
            boundary.Payload.ToArray());
        CollectionAssert.AreEqual(
            new byte[] { 9, 8, 7 },
            afterOversized.Payload.ToArray());
        Assert.IsFalse(source.Output.TryRead(out _));
        Assert.AreEqual(
            new DatagramSourceCounters(3, 2, 0, 1, 0),
            source.Counters);
    }

    [TestMethod]
    public async Task Saturation_DropsWithoutBlockingAndPreservesSequenceGaps()
    {
        await using var source = CreateSource(channelCapacity: 1);
        await source.StartAsync();
        var endpoint = GetBoundEndpoint(source);

        await SendAsync(endpoint, [0x01]);
        await WaitUntilAsync(() => source.Counters.SourceEnqueued == 1);
        await SendAsync(endpoint, [0x02]);
        await WaitUntilAsync(() => source.Counters.SourceDroppedFull == 1);
        var first = await source.Output.ReadAsync();
        await SendAsync(endpoint, [0x03]);
        await WaitUntilAsync(() => source.Counters.SourceEnqueued == 2);
        var third = await source.Output.ReadAsync();
        await source.StopAsync();

        Assert.AreEqual(1L, first.Sequence);
        Assert.AreEqual(3L, third.Sequence);
        CollectionAssert.AreEqual(new byte[] { 0x01 }, first.Payload.ToArray());
        CollectionAssert.AreEqual(new byte[] { 0x03 }, third.Payload.ToArray());
        Assert.AreEqual(
            new DatagramSourceCounters(3, 2, 1, 0, 0),
            source.Counters);
    }

    [TestMethod]
    public async Task StartAndConcurrentStop_AreIdempotentAndCompleteOutputOnce()
    {
        await using var source = CreateSource();

        await Task.WhenAll(source.StartAsync(), source.StartAsync());
        await Task.WhenAll(
            source.StopAsync(),
            source.StopAsync(),
            source.StopAsync());
        await source.StopAsync();

        await source.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(source.LocalEndpoint);
        Assert.AreEqual(default, source.Counters);
    }

    [TestMethod]
    public async Task Admission_IsVisibleInTheLedgerBeforeConsumerAccounting()
    {
        await using var source = CreateSource(channelCapacity: 1);
        await source.StartAsync();
        var endpoint = GetBoundEndpoint(source);

        for (var sequence = 1; sequence <= 250; sequence++)
        {
            await SendAsync(endpoint, [(byte)sequence]);
            var envelope = await source.Output.ReadAsync().AsTask().WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.AreEqual(sequence, envelope.Sequence);
            Assert.IsGreaterThanOrEqualTo(
                source.Counters.SourceEnqueued,
                envelope.Sequence);
        }

        await source.StopAsync();
        Assert.AreEqual(
            new DatagramSourceCounters(250, 250, 0, 0, 0),
            source.Counters);
    }

    [TestMethod]
    public async Task FatalReceiveError_RetiresTheSocketAndCannotMasqueradeAsStarted()
    {
        using var failure = new ControlledReceiveFailure();
        await using var source = new UdpDatagramSource(
            new UdpDatagramSourceOptions(port: 0),
            () => failure.Socket,
            failure.ReceiveAsync);
        await source.StartAsync();
        var retiredEndpoint = GetBoundEndpoint(source);

        await failure.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        failure.Release();

        var completionFailure = await ObserveCompletionFailureAsync(
            source.Output.Completion);

        Assert.IsTrue(
            completionFailure is SocketException
            {
                SocketErrorCode: SocketError.ConnectionReset,
            },
            $"Unexpected completion failure: {completionFailure}");
        Assert.IsNull(source.LocalEndpoint);
        Assert.AreEqual(1L, source.Counters.SocketErrors);
        using var replacement = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
        };
        replacement.Bind(retiredEndpoint);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => source.StartAsync());
        await source.StopAsync();
        await source.StopAsync();
    }

    [TestMethod]
    public async Task FatalReceiveRacingStopAndDispose_ReturnsAfterChannelCompletion()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            using var failure = new ControlledReceiveFailure();
            var source = new UdpDatagramSource(
                new UdpDatagramSourceOptions(port: 0),
                () => failure.Socket,
                failure.ReceiveAsync);
            await source.StartAsync();

            await failure.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            var stop = source.StopAsync();
            var dispose = source.DisposeAsync().AsTask();
            failure.Release();

            await Task.WhenAll(stop, dispose).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(source.Output.Completion.IsCompleted);
            var completionFailure = await ObserveCompletionFailureAsync(
                source.Output.Completion);
            Assert.IsInstanceOfType<SocketException>(completionFailure);
            Assert.AreEqual(1L, source.Counters.SocketErrors);
            Assert.IsNull(source.LocalEndpoint);
            await source.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task RacingStartStopAndDispose_AlwaysResolvesOwnership()
    {
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var source = CreateSource();
            using var barrier = new Barrier(4);
            Exception? startFailure = null;
            var start = Task.Run(
                async () =>
                {
                    barrier.SignalAndWait();
                    try
                    {
                        await source.StartAsync();
                    }
                    catch (Exception exception)
                        when (exception is InvalidOperationException
                              or ObjectDisposedException)
                    {
                        startFailure = exception;
                    }
                });
            var stop = Task.Run(
                async () =>
                {
                    barrier.SignalAndWait();
                    await source.StopAsync();
                });
            var dispose = Task.Run(
                async () =>
                {
                    barrier.SignalAndWait();
                    await source.DisposeAsync();
                });

            barrier.SignalAndWait();
            await Task.WhenAll(start, stop, dispose).WaitAsync(
                TimeSpan.FromSeconds(5));
            await source.DisposeAsync();

            Assert.IsTrue(
                startFailure is null
                or InvalidOperationException
                or ObjectDisposedException);
            Assert.IsNull(source.LocalEndpoint);
            await source.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task StopBeforeStart_IsTerminalAndCompletesOutput()
    {
        await using var source = CreateSource();

        await source.StopAsync();

        await source.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => source.StartAsync());
    }

    [TestMethod]
    public async Task BindFailure_DoesNotConsumeTheSourceAndCanBeRetried()
    {
        using var blocker = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var blockedEndpoint = (IPEndPoint)blocker.LocalEndPoint!;
        await using var source = new UdpDatagramSource(
            new UdpDatagramSourceOptions(port: blockedEndpoint.Port));

        await Assert.ThrowsExactlyAsync<SocketException>(
            () => source.StartAsync());
        Assert.IsNull(source.LocalEndpoint);
        blocker.Dispose();
        await source.StartAsync();

        Assert.AreEqual(
            new IPEndPoint(IPAddress.Loopback, blockedEndpoint.Port),
            source.LocalEndpoint);
        await source.StopAsync();
    }

    [TestMethod]
    public async Task CancelledStartCanBeRetriedAndDisposePreventsFutureStart()
    {
        var source = CreateSource();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.StartAsync(cancellation.Token));
        await source.StartAsync();
        await source.DisposeAsync();

        await source.Output.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => source.StartAsync());
        await source.DisposeAsync();
    }

    private static UdpDatagramSource CreateSource(
        int channelCapacity = 256,
        int maximumDatagramBytes = UdpDatagramLimits.MaximumPayloadLength) =>
        new(
            new UdpDatagramSourceOptions(
                port: 0,
                channelCapacity,
                maximumDatagramBytes));

    private static IPEndPoint GetBoundEndpoint(UdpDatagramSource source) =>
        source.LocalEndpoint
        ?? throw new AssertFailedException("The source did not publish its bound endpoint.");

    private static async Task SendAsync(IPEndPoint endpoint, byte[] payload)
    {
        using var sender = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        var sent = await sender.SendToAsync(payload, SocketFlags.None, endpoint);
        Assert.AreEqual(payload.Length, sent);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(5))
            {
                Assert.Fail("Timed out waiting for the UDP source condition.");
            }

            await Task.Delay(10);
        }
    }

    private static async Task<Exception> ObserveCompletionFailureAsync(
        Task completion)
    {
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception)
        {
            if (exception is TimeoutException)
            {
                throw new AssertFailedException(
                    "Timed out waiting for the source completion failure.",
                    exception);
            }

            return exception;
        }

        throw new AssertFailedException(
            "The source channel completed successfully after a fatal receive error.");
    }

    private sealed class ControlledReceiveFailure : IDisposable
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Socket Socket { get; } = new(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);

        public Task Entered => _entered.Task;

        public void Release() => _release.TrySetResult();

        public async ValueTask<SocketReceiveMessageFromResult> ReceiveAsync(
            Socket socket,
            Memory<byte> buffer,
            EndPoint remoteEndpoint,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            throw new SocketException((int)SocketError.ConnectionReset);
        }

        public void Dispose() => Socket.Dispose();
    }
}
