using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
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
        var targetRate = ReadOptionalInt(
            "APEXLAB_SOAK_TARGET_DATAGRAMS_PER_SECOND",
            maximum: 100_000);
        var measuredRealPeakRate = ReadOptionalInt(
            "APEXLAB_SOAK_MEASURED_REAL_PEAK_DATAGRAMS_PER_SECOND",
            maximum: 50_000);
        var minimumObservedRate = checked(2 * measuredRealPeakRate);
        var minimumObservedFractionPermille = ReadOptionalInt(
            "APEXLAB_SOAK_MINIMUM_OBSERVED_FRACTION_PERMILLE",
            maximum: 1_000,
            defaultValue: 950,
            minimum: 950);
        if (targetRate == 0 && minimumObservedRate != 0)
        {
            throw new InvalidOperationException(
                "A minimum observed rate requires a non-zero target rate.");
        }
        if (minimumObservedRate > targetRate)
        {
            throw new InvalidOperationException(
                "The minimum observed rate cannot exceed the target rate.");
        }
        if (minimumObservedRate > 0)
        {
            var minimumTargetRate = checked(
                (minimumObservedRate * 11L + 9L) / 10L);
            if (targetRate < minimumTargetRate)
            {
                throw new InvalidOperationException(
                    "The rate gate target must be at least 110% of its minimum observed rate.");
            }
            if (requested < targetRate * 60L)
            {
                throw new InvalidOperationException(
                    "The rate gate must run for at least 60 seconds at its target rate.");
            }
        }
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
        var measurement = Stopwatch.StartNew();
        for (var index = 0; index < requested; index++)
        {
            random.NextBytes(payload.AsSpan(0, 64));
            var length = index % 17 == 0 ? payload.Length : 64;
            await sender.SendAsync(
                payload.AsMemory(0, length),
                endpoint,
                TestContext.CancellationToken);
            if (targetRate > 0)
            {
                var targetElapsed = TimeSpan.FromSeconds(
                    (index + 1d) / targetRate);
                var remaining = targetElapsed - measurement.Elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(
                        remaining,
                        TestContext.CancellationToken);
                }
            }
            if ((index & 1_023) == 0)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
            }
        }
        var injectionSeconds = measurement.Elapsed.TotalSeconds;

        var drainDeadline = Stopwatch.StartNew();
        while (source.Counters.DatagramsObserved < requested
               && drainDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(5),
                TestContext.CancellationToken);
        }
        measurement.Stop();
        var observationSeconds = measurement.Elapsed.TotalSeconds;
        var rateWindowObserved = source.Counters.DatagramsObserved;
        await source.StopAsync(TestContext.CancellationToken);
        await consumer.WaitAsync(TestContext.CancellationToken);
        peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: false));
        var counters = source.Counters;
        var managedGrowth = Math.Max(0L, peak - baseline);
        var achievedSendRate = requested / injectionSeconds;
        var achievedObservedRate = rateWindowObserved / observationSeconds;
        var minimumObserved = checked(
            (requested * (long)minimumObservedFractionPermille + 999L) / 1_000L);

        TestContext.WriteLine($"capture-soak-observed={counters.DatagramsObserved}");
        TestContext.WriteLine($"capture-soak-enqueued={counters.SourceEnqueued}");
        TestContext.WriteLine($"capture-soak-dropped={counters.SourceDroppedFull}");
        TestContext.WriteLine($"capture-soak-managed-growth-bytes={managedGrowth}");
        TestContext.WriteLine($"capture-soak-injection-seconds={injectionSeconds:F3}");
        TestContext.WriteLine($"capture-soak-observation-seconds={observationSeconds:F3}");
        TestContext.WriteLine($"capture-soak-rate-window-observed={rateWindowObserved}");
        TestContext.WriteLine($"capture-soak-send-rate={achievedSendRate:F1}");
        TestContext.WriteLine($"capture-soak-observed-rate={achievedObservedRate:F1}");

        await WriteSummaryIfRequestedAsync(
            new SoakSummary(
                SoakSeed,
                requested,
                targetRate,
                minimumObservedRate,
                minimumObservedFractionPermille,
                Math.Round(injectionSeconds, 3),
                Math.Round(observationSeconds, 3),
                Math.Round(achievedSendRate, 1),
                Math.Round(achievedObservedRate, 1),
                rateWindowObserved,
                counters.DatagramsObserved,
                counters.SourceEnqueued,
                counters.SourceDroppedFull,
                counters.SourceRejectedOversized,
                managedGrowth,
                MaximumManagedGrowthBytes),
            TestContext.CancellationToken);

        Assert.IsGreaterThan(0L, counters.DatagramsObserved);
        Assert.IsGreaterThan(0L, counters.SourceEnqueued);
        Assert.IsGreaterThanOrEqualTo(
            minimumObserved,
            counters.DatagramsObserved,
            "Loopback observation fell below the reviewed delivery fraction.");
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
        if (minimumObservedRate > 0)
        {
            Assert.IsGreaterThanOrEqualTo(
                minimumObservedRate,
                achievedObservedRate,
                "The achieved observed rate did not satisfy the configured gate.");
        }
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

    private static int ReadOptionalInt(
        string variable,
        int maximum,
        int defaultValue = 0,
        int minimum = 0)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return defaultValue;
        }
        if (!int.TryParse(
                configured,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidOperationException(
                $"{variable} must be between {minimum} and {maximum}.");
        }
        return value;
    }

    private static async Task WriteSummaryIfRequestedAsync(
        SoakSummary summary,
        CancellationToken cancellationToken)
    {
        var path = Environment.GetEnvironmentVariable(
            "APEXLAB_SOAK_SUMMARY_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var json = JsonSerializer.Serialize(
            summary,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    private sealed record SoakSummary(
        int Seed,
        int RequestedDatagrams,
        int TargetDatagramsPerSecond,
        int MinimumObservedDatagramsPerSecond,
        int MinimumObservedFractionPermille,
        double InjectionSeconds,
        double ObservationSeconds,
        double AchievedSendDatagramsPerSecond,
        double AchievedObservedDatagramsPerSecond,
        long RateWindowObservedDatagrams,
        long DatagramsObserved,
        long SourceEnqueued,
        long SourceDroppedFull,
        long SourceRejectedOversized,
        long ManagedGrowthBytes,
        long MaximumManagedGrowthBytes);
}
