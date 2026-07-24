using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ApexLab.Replay.Probe;

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

    public TestContext TestContext { get; set; } = null!;
}
