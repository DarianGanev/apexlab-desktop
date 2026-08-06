using System.Net.Sockets;
using ApexLab.Telemetry.Udp;

namespace ApexLab.IntegrationTests.Capture;

[TestClass]
public sealed class CaptureSoakTests
{
    private const int SoakSeed = 25_082_026;
    private const int DefaultDatagramCount = 100_000;
    private const long MaximumManagedGrowthBytes = 128L * 1024L * 1024L;

    [TestMethod]
    [TestCategory("Soak")]
    [Timeout(180_000, CooperativeCancellation = true)]
    public async Task SustainedLoopbackOverloadRetainsBoundedMemoryAndExactSourceAccounting()
    {
        var requested = ReadDatagramCount();
        TestContext.WriteLine($"capture-soak-seed={SoakSeed}");
        TestContext.WriteLine($"capture-soak-requested={requested}");
        await using var source = new UdpDatagramSource(
            new UdpDatagramSourceOptions(
                port: 0,
                channelCapacity: 256,
                maximumDatagramBytes: 1_500));
        await source.StartAsync(TestContext.CancellationToken);
        var endpoint = source.LocalEndpoint;
        Assert.IsNotNull(endpoint);

        var consumed = 0L;
        var lastSequence = 0L;
        var ordered = true;
        var consumer = Task.Run(
            async () =>
            {
                await foreach (var envelope in source.Output.ReadAllAsync(
                                   TestContext.CancellationToken))
                {
                    ordered &= envelope.Sequence > lastSequence;
                    lastSequence = envelope.Sequence;
                    consumed++;
                    if ((consumed & 255L) == 0)
                    {
                        await Task.Yield();
                    }
                }
            },
            TestContext.CancellationToken);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        var peak = baseline;
        var random = new Random(SoakSeed);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var payload = new byte[1_500];
        for (var index = 0; index < requested; index++)
        {
            random.NextBytes(payload.AsSpan(0, 64));
            var length = index % 17 == 0 ? payload.Length : 64;
            await sender.SendAsync(
                payload.AsMemory(0, length),
                endpoint,
                TestContext.CancellationToken);
            if ((index & 1_023) == 0)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            }
        }

        await Task.Delay(
            TimeSpan.FromMilliseconds(500),
            TestContext.CancellationToken);
        await source.StopAsync(TestContext.CancellationToken);
        await consumer.WaitAsync(TestContext.CancellationToken);
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
        var counters = source.Counters;
        var managedGrowth = Math.Max(0L, peak - baseline);

        TestContext.WriteLine($"capture-soak-observed={counters.DatagramsObserved}");
        TestContext.WriteLine($"capture-soak-enqueued={counters.SourceEnqueued}");
        TestContext.WriteLine($"capture-soak-dropped={counters.SourceDroppedFull}");
        TestContext.WriteLine($"capture-soak-managed-growth-bytes={managedGrowth}");
        Assert.IsGreaterThan(0L, counters.DatagramsObserved);
        Assert.IsGreaterThan(0L, counters.SourceEnqueued);
        Assert.AreEqual(counters.SourceEnqueued, consumed);
        Assert.IsTrue(ordered);
        Assert.IsLessThanOrEqualTo(lastSequence, counters.DatagramsObserved);
        Assert.AreEqual(
            counters.DatagramsObserved,
            counters.SourceEnqueued
            + counters.SourceDroppedFull
            + counters.SourceRejectedOversized);
        Assert.IsLessThan(
            MaximumManagedGrowthBytes,
            managedGrowth,
            "The bounded source exceeded the reviewed managed-memory envelope.");
    }

    public TestContext TestContext { get; set; } = null!;

    private static int ReadDatagramCount()
    {
        var configured = Environment.GetEnvironmentVariable(
            "APEXLAB_SOAK_DATAGRAMS");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultDatagramCount;
        }
        if (!int.TryParse(
                configured,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var count)
            || count is < 10_000 or > 5_000_000)
        {
            throw new InvalidOperationException(
                "APEXLAB_SOAK_DATAGRAMS must be between 10000 and 5000000.");
        }

        return count;
    }
}
