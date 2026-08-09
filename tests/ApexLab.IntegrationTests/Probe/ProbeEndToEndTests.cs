using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ApexLab.Replay.Probe;

namespace ApexLab.IntegrationTests.Probe;

[TestClass]
public sealed class ProbeEndToEndTests
{
    private const ulong SyntheticSessionUid = 0x76543210FEDCBA98;

    [TestMethod]
    public async Task SyntheticLoopbackEventPacketProducesCompatibleAggregateOnlyReport()
    {
        var port = ReserveAvailablePort();
        var commandTask = ProbeCommand.ExecuteAsync(
            [
                "probe",
                "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--duration-seconds", "1",
            ],
            TestContext.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        var datagram = CreateEventDatagram();

        while (!commandTask.IsCompleted)
        {
            await sender.SendAsync(datagram, datagram.Length, destination);
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.CancellationToken);
        }

        var result = await commandTask;
        using var document = JsonDocument.Parse(result.Json);
        var root = document.RootElement;

        Assert.AreEqual(ProbeExitCode.Success, result.ExitCode);
        Assert.AreEqual("success", root.GetProperty("status").GetString());
        Assert.IsGreaterThan(
            0,
            root.GetProperty("classification").GetProperty("compatible").GetInt64());
        Assert.AreEqual(
            0L,
            root.GetProperty("classification").GetProperty("invalidPacketLength").GetInt64());
        Assert.IsGreaterThan(0, root.GetProperty("packetShapes").GetArrayLength());
        var shape = root.GetProperty("packetShapes")[0];
        Assert.AreEqual(3, shape.GetProperty("packetId").GetByte());
        Assert.AreEqual(1, shape.GetProperty("packetVersion").GetByte());
        Assert.AreEqual(45, shape.GetProperty("datagramLength").GetInt32());
        Assert.AreEqual(
            1,
            root.GetProperty("headers").GetProperty("sessionUidCardinality").GetInt32());
        Assert.DoesNotContain(
            SyntheticSessionUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            result.Json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Convert.ToHexString(datagram),
            result.Json,
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task NoPacketsReturnsNoTrafficExitCode()
    {
        var port = ReserveAvailablePort();

        var result = await ProbeCommand.ExecuteAsync(
            [
                "probe",
                "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--duration-seconds", "1",
            ],
            TestContext.CancellationToken);
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.NoTraffic, result.ExitCode);
        Assert.AreEqual("noTraffic", document.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(
            0L,
            document.RootElement
                .GetProperty("source")
                .GetProperty("datagramsObserved")
                .GetInt64());
    }

    [TestMethod]
    public async Task MalformedPacketsReturnIncompatibleOnlyExitCode()
    {
        var port = ReserveAvailablePort();
        var commandTask = ProbeCommand.ExecuteAsync(
            [
                "probe",
                "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--duration-seconds", "1",
            ],
            TestContext.CancellationToken);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var destination = new IPEndPoint(IPAddress.Loopback, port);
        var malformed = new byte[1];

        while (!commandTask.IsCompleted)
        {
            await sender.SendAsync(malformed, malformed.Length, destination);
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.CancellationToken);
        }

        var result = await commandTask;
        using var document = JsonDocument.Parse(result.Json);

        Assert.AreEqual(ProbeExitCode.IncompatibleOnlyTraffic, result.ExitCode);
        Assert.AreEqual(
            "incompatibleOnlyTraffic",
            document.RootElement.GetProperty("status").GetString());
        Assert.IsGreaterThan(
            0L,
            document.RootElement
                .GetProperty("classification")
                .GetProperty("malformedHeader")
                .GetInt64());
        Assert.AreEqual(
            0L,
            document.RootElement
                .GetProperty("classification")
                .GetProperty("compatible")
                .GetInt64());
    }

    public TestContext TestContext { get; set; } = null!;

    private static int ReserveAvailablePort()
    {
        using var reservation = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp)
        {
            ExclusiveAddressUse = true,
        };
        reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)reservation.LocalEndPoint!).Port;
    }

    private static byte[] CreateEventDatagram()
    {
        var datagram = new byte[45];
        BinaryPrimitives.WriteUInt16LittleEndian(datagram, 2025);
        datagram[2] = 25;
        datagram[3] = 1;
        datagram[4] = 7;
        datagram[5] = 1;
        datagram[6] = 3;
        BinaryPrimitives.WriteUInt64LittleEndian(
            datagram.AsSpan(7),
            SyntheticSessionUid);
        BinaryPrimitives.WriteInt32LittleEndian(
            datagram.AsSpan(15),
            BitConverter.SingleToInt32Bits(12.5F));
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(19), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(datagram.AsSpan(23), 100);
        datagram[27] = 0;
        datagram[28] = byte.MaxValue;
        "SSTA"u8.CopyTo(datagram.AsSpan(29));
        return datagram;
    }
}
