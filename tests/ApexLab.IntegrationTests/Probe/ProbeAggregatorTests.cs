using System.Text.Json;
using ApexLab.Application.Capture;
using ApexLab.Replay.Probe;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.IntegrationTests.Probe;

[TestClass]
public sealed class ProbeAggregatorTests
{
    private const ulong PrivateSessionUid = 0x0123456789ABCDEF;

    [TestMethod]
    public void JsonContainsOnlyAggregateSchemaAndNeverSessionIdentityOrPayload()
    {
        var aggregator = new ProbeAggregator();
        aggregator.Observe(
            CreateObservation(
                sequence: 1,
                monotonicTimestamp: 100,
                receivedMilliseconds: 100,
                sessionUid: PrivateSessionUid,
                sessionTime: 10,
                frame: 10,
                overallFrame: 20,
                playerIndex: 2,
                secondaryIndex: byte.MaxValue));
        aggregator.Observe(
            CreateObservation(
                sequence: 3,
                monotonicTimestamp: 90,
                receivedMilliseconds: 50,
                sessionUid: PrivateSessionUid,
                sessionTime: 9,
                frame: 8,
                overallFrame: 25,
                playerIndex: 5,
                secondaryIndex: 1));
        aggregator.Observe(
            CreateObservation(
                sequence: 4,
                monotonicTimestamp: 110,
                receivedMilliseconds: 1_250,
                sessionUid: 0x1111222233334444,
                sessionTime: 1,
                frame: 1,
                overallFrame: 1,
                playerIndex: 3,
                secondaryIndex: byte.MaxValue));
        var counters = CreateCounters(observed: 3, compatible: 3);

        var json = ProbeJson.Serialize(
            aggregator.BuildReport(
                "success",
                "ea-f1-25-v3",
                TimeSpan.FromSeconds(2),
                counters));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        CollectionAssert.AreEquivalent(
            new[]
            {
                "schemaVersion",
                "status",
                "protocolId",
                "durationMilliseconds",
                "source",
                "classification",
                "packetShapes",
                "descriptors",
                "rateBuckets",
                "sequence",
                "headers",
                "playerIndices",
            },
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(2, root.GetProperty("headers").GetProperty("sessionUidCardinality").GetInt32());
        Assert.AreEqual(1L, root.GetProperty("sequence").GetProperty("gaps").GetInt64());
        Assert.AreEqual(
            1L,
            root.GetProperty("sequence").GetProperty("monotonicTimestampRegressions").GetInt64());
        Assert.AreEqual(
            1L,
            root.GetProperty("sequence").GetProperty("utcTimestampRegressions").GetInt64());
        Assert.AreEqual(
            1L,
            root.GetProperty("headers").GetProperty("sessionTimeRegressions").GetInt64());
        Assert.AreEqual(
            1L,
            root.GetProperty("headers").GetProperty("frameRegressions").GetInt64());
        Assert.AreEqual(
            4L,
            root.GetProperty("headers")
                .GetProperty("overallFrameSkippedIdentifierValues")
                .GetInt64());
        Assert.AreEqual(
            2,
            root.GetProperty("playerIndices").GetProperty("playerMinimum").GetByte());
        Assert.AreEqual(
            5,
            root.GetProperty("playerIndices").GetProperty("playerMaximum").GetByte());
        Assert.AreEqual(
            2L,
            root.GetProperty("playerIndices").GetProperty("secondaryAbsentCount").GetInt64());

        var propertyNames = EnumeratePropertyNames(root).ToArray();
        CollectionAssert.DoesNotContain(propertyNames, "payload");
        CollectionAssert.DoesNotContain(propertyNames, "sender");
        CollectionAssert.DoesNotContain(propertyNames, "senderAddress");
        CollectionAssert.DoesNotContain(propertyNames, "senderPort");
        CollectionAssert.DoesNotContain(propertyNames, "sessionUid");
        Assert.DoesNotContain(
            PrivateSessionUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            json,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void PacketShapesAndDescriptorsAreDeterministicallySortedAndCounted()
    {
        var aggregator = new ProbeAggregator();
        aggregator.Observe(
            CreateObservation(
                sequence: 1,
                monotonicTimestamp: 1,
                receivedMilliseconds: 0,
                sessionUid: 1,
                sessionTime: 1,
                frame: 1,
                overallFrame: 1,
                playerIndex: 0,
                secondaryIndex: byte.MaxValue,
                packetId: 6,
                datagramLength: 1_352));
        aggregator.Observe(
            CreateObservation(
                sequence: 2,
                monotonicTimestamp: 2,
                receivedMilliseconds: 1,
                sessionUid: 1,
                sessionTime: 2,
                frame: 2,
                overallFrame: 2,
                playerIndex: 0,
                secondaryIndex: byte.MaxValue,
                packetId: 6,
                datagramLength: 1_352));

        var report = aggregator.BuildReport(
            "success",
            "ea-f1-25-v3",
            TimeSpan.FromSeconds(1),
            CreateCounters(observed: 2, compatible: 2));

        Assert.HasCount(1, report.PacketShapes);
        Assert.AreEqual((byte)6, report.PacketShapes[0].PacketId);
        Assert.AreEqual(1_352, report.PacketShapes[0].DatagramLength);
        Assert.AreEqual(2L, report.PacketShapes[0].Count);
        Assert.HasCount(1, report.Descriptors);
        Assert.AreEqual(2L, report.Descriptors[0].Count);
    }

    private static CapturePacketObservation CreateObservation(
        long sequence,
        long monotonicTimestamp,
        long receivedMilliseconds,
        ulong sessionUid,
        float sessionTime,
        uint frame,
        uint overallFrame,
        byte playerIndex,
        byte secondaryIndex,
        byte packetId = 6,
        int datagramLength = 1_352)
    {
        var descriptor = new TelemetryPacketDescriptor(
            "Synthetic",
            packetId,
            packetVersion: 1,
            datagramLength,
            TelemetryPacketPrivacyDisposition.EvidenceAllowed);
        var header = new TelemetryHeaderMetadata(
            packetFormat: 2025,
            gameYear: 25,
            gameMajorVersion: 1,
            gameMinorVersion: 7,
            packetVersion: 1,
            packetId,
            sessionUid,
            sessionTime,
            frame,
            overallFrame,
            playerIndex,
            secondaryIndex);
        return new CapturePacketObservation(
            sequence,
            monotonicTimestamp,
            DateTimeOffset.UnixEpoch.AddMilliseconds(receivedMilliseconds),
            datagramLength,
            TelemetryPacketResult.Compatible(header, descriptor));
    }

    private static CaptureIngestionCounters CreateCounters(
        long observed,
        long compatible)
    {
        return new(
            new DatagramSourceCounters(observed, observed, 0, 0, 0),
            new DatagramClassificationCounters(
                sourceDequeued: observed,
                compatible,
                malformedHeader: observed - compatible,
                unsupportedFormat: 0,
                unsupportedYear: 0,
                unknownPacketId: 0,
                unsupportedPacketVersion: 0,
                invalidPacketLength: 0,
                excludedPrivacyPacket: 0,
                unexpectedSender: 0,
                classifierAbandonedOnInterrupt: 0));
    }

    private static IEnumerable<string> EnumeratePropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var nested in EnumeratePropertyNames(property.Value))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in EnumeratePropertyNames(item))
                {
                    yield return nested;
                }
            }
        }
    }
}
