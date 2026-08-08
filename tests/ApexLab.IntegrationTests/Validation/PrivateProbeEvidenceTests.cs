using ApexLab.Protocols.F125;
using ApexLab.Replay.Probe;
using ApexLab.Replay.Validation;

namespace ApexLab.IntegrationTests.Validation;

[TestClass]
public sealed class PrivateProbeEvidenceTests
{
    [TestMethod]
    public async Task AcceptsCompleteBaseProtocolProbeAndReturnsItsPeak()
    {
        using var reportFile = TemporaryReport.Create(
            ProbeJson.Serialize(CreateAcceptedReport()));

        var evaluation = await PrivateProbeEvidence.ReadAndEvaluateAsync(
            reportFile.Path,
            TestContext.CancellationToken);

        Assert.AreEqual(F125Protocol.Id, evaluation.ProtocolId);
        Assert.AreEqual(3, evaluation.MeasuredPeakDatagramsPerSecond);
        Assert.HasCount(2, evaluation.ObservedDescriptors);
        Assert.Contains(
            new PrivateDescriptorShape(0, 1, 1_349),
            evaluation.ObservedDescriptors);
        Assert.Contains(
            new PrivateDescriptorShape(4, 1, 1_284),
            evaluation.ObservedDescriptors);
    }

    [TestMethod]
    public async Task RejectsUnsupportedSchemaWithoutDisclosingPath()
    {
        using var reportFile = TemporaryReport.Create(
            ProbeJson.Serialize(CreateAcceptedReport() with
            {
                SchemaVersion = ProbeAggregator.SchemaVersion + 1,
            }));

        var exception = await Assert.ThrowsAsync<PrivateProbeFailureException>(
            () => PrivateProbeEvidence.ReadAndEvaluateAsync(
                reportFile.Path,
                TestContext.CancellationToken));

        Assert.AreEqual(
            PrivateProbeFailureKind.UnsupportedSchema,
            exception.Kind);
        Assert.DoesNotContain(
            reportFile.Path,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public async Task RejectsProbeThatDidNotCompleteSuccessfully()
    {
        await AssertFailureAsync(
            CreateAcceptedReport() with { Status = "noTraffic" },
            PrivateProbeFailureKind.NoCompatibleTraffic);
    }

    [TestMethod]
    public async Task RejectsAProtocolOtherThanTheRecordedBaseAdapter()
    {
        await AssertFailureAsync(
            CreateAcceptedReport() with { ProtocolId = "other-protocol" },
            PrivateProbeFailureKind.ProtocolMismatch);
    }

    [TestMethod]
    public async Task RejectsProbeWithoutCompatibleTraffic()
    {
        var accepted = CreateAcceptedReport();
        await AssertFailureAsync(
            accepted with
            {
                Classification = accepted.Classification with
                {
                    Compatible = 0,
                    ExcludedPrivacyPacket = 4,
                },
            },
            PrivateProbeFailureKind.NoCompatibleTraffic);
    }

    [TestMethod]
    public async Task RejectsInconsistentSourceOrClassifierAccounting()
    {
        var accepted = CreateAcceptedReport();
        ProbeReport[] invalid =
        [
            accepted with
            {
                Source = accepted.Source with { DatagramsObserved = 5 },
            },
            accepted with
            {
                Classification = accepted.Classification with
                {
                    Compatible = 2,
                },
            },
        ];

        foreach (var report in invalid)
        {
            await AssertFailureAsync(
                report,
                PrivateProbeFailureKind.AccountingMismatch);
        }
    }

    [TestMethod]
    public async Task RejectsEverySourceLossAndClassifierErrorCounter()
    {
        var accepted = CreateAcceptedReport();
        ProbeReport[] rejected =
        [
            accepted with
            {
                Source = accepted.Source with
                {
                    SourceEnqueued = 3,
                    SourceDroppedFull = 1,
                },
                Classification = accepted.Classification with
                {
                    SourceDequeued = 3,
                    Compatible = 2,
                },
            },
            accepted with
            {
                Source = accepted.Source with
                {
                    SourceEnqueued = 3,
                    SourceRejectedOversized = 1,
                },
                Classification = accepted.Classification with
                {
                    SourceDequeued = 3,
                    Compatible = 2,
                },
            },
            accepted with
            {
                Source = accepted.Source with { SocketErrors = 1 },
            },
            WithClassifierError(
                accepted,
                accepted.Classification with
                {
                    Compatible = 2,
                    MalformedHeader = 1,
                }),
            WithClassifierError(
                accepted,
                accepted.Classification with
                {
                    Compatible = 2,
                    UnsupportedFormat = 1,
                }),
            WithClassifierError(
                accepted,
                accepted.Classification with
                {
                    Compatible = 2,
                    UnsupportedYear = 1,
                }),
            WithClassifierError(
                accepted,
                accepted.Classification with
                {
                    Compatible = 2,
                    UnknownPacketId = 1,
                }),
            WithClassifierError(
                accepted,
                accepted.Classification with
                {
                    Compatible = 2,
                    UnsupportedPacketVersion = 1,
                }),
            WithClassifierError(
                accepted,
                accepted.Classification with
                {
                    Compatible = 2,
                    InvalidPacketLength = 1,
                }),
            WithClassifierError(
                accepted,
                accepted.Classification with
                {
                    Compatible = 2,
                    UnexpectedSender = 1,
                }),
            accepted with
            {
                Classification = accepted.Classification with
                {
                    SourceDequeued = 3,
                    Compatible = 2,
                    ClassifierAbandonedOnTermination = 1,
                },
            },
        ];

        foreach (var report in rejected)
        {
            await AssertFailureAsync(
                report,
            PrivateProbeFailureKind.RejectedTraffic);
        }
    }

    [TestMethod]
    public async Task RejectsEverySequenceTimestampAndHeaderRegression()
    {
        var accepted = CreateAcceptedReport();
        ProbeReport[] rejected =
        [
            accepted with
            {
                Sequence = accepted.Sequence with { Regressions = 1 },
            },
            accepted with
            {
                Sequence = accepted.Sequence with
                {
                    MonotonicTimestampRegressions = 1,
                },
            },
            accepted with
            {
                Sequence = accepted.Sequence with
                {
                    UtcTimestampRegressions = 1,
                },
            },
            accepted with
            {
                Headers = accepted.Headers with
                {
                    SessionTimeRegressions = 1,
                },
            },
            accepted with
            {
                Headers = accepted.Headers with { FrameRegressions = 1 },
            },
            accepted with
            {
                Headers = accepted.Headers with
                {
                    OverallFrameRegressions = 1,
                },
            },
        ];

        foreach (var report in rejected)
        {
            await AssertFailureAsync(
                report,
            PrivateProbeFailureKind.Regression);
        }
    }

    [TestMethod]
    public async Task RejectsDescriptorsThatDisagreeWithCatalogOrPacketShapes()
    {
        var accepted = CreateAcceptedReport();
        ProbeReport[] rejected =
        [
            accepted with
            {
                Descriptors =
                [
                    new ProbeDescriptorReport(0, 2, 1_349, 3),
                    new ProbeDescriptorReport(4, 1, 1_284, 1),
                ],
            },
            accepted with
            {
                PacketShapes =
                [
                    new ProbePacketShapeReport(0, 1, 1_348, 3),
                    new ProbePacketShapeReport(4, 1, 1_284, 1),
                ],
            },
            accepted with
            {
                Descriptors =
                [
                    new ProbeDescriptorReport(0, 1, 1_349, 2),
                    new ProbeDescriptorReport(4, 1, 1_284, 1),
                ],
            },
            accepted with
            {
                Descriptors =
                [
                    new ProbeDescriptorReport(0, 1, 1_349, 2),
                    new ProbeDescriptorReport(0, 1, 1_349, 1),
                    new ProbeDescriptorReport(4, 1, 1_284, 1),
                ],
            },
            accepted with
            {
                Descriptors =
                [
                    new ProbeDescriptorReport(99, 1, 1_349, 3),
                    new ProbeDescriptorReport(4, 1, 1_284, 1),
                ],
                PacketShapes =
                [
                    new ProbePacketShapeReport(99, 1, 1_349, 3),
                    new ProbePacketShapeReport(4, 1, 1_284, 1),
                ],
            },
        ];

        foreach (var report in rejected)
        {
            await AssertFailureAsync(
                report,
            PrivateProbeFailureKind.DescriptorMismatch);
        }
    }

    [TestMethod]
    public async Task RejectsInvalidOrNonpositiveRateBuckets()
    {
        var accepted = CreateAcceptedReport();
        ProbeReport[] malformed =
        [
            accepted with
            {
                RateBuckets =
                [
                    new ProbeRateBucketReport(0, 1),
                    new ProbeRateBucketReport(0, 3),
                ],
            },
            accepted with
            {
                RateBuckets =
                [
                    new ProbeRateBucketReport(-1, 1),
                    new ProbeRateBucketReport(0, 3),
                ],
            },
            accepted with
            {
                RateBuckets =
                [
                    new ProbeRateBucketReport(0, 1),
                    new ProbeRateBucketReport(1, 2),
                ],
            },
        ];

        foreach (var report in malformed)
        {
            await AssertFailureAsync(
                report,
                PrivateProbeFailureKind.Malformed);
        }

        await AssertFailureAsync(
            accepted with
            {
                RateBuckets = [new ProbeRateBucketReport(0, 0)],
            },
            PrivateProbeFailureKind.NoCompatibleTraffic);
        await AssertFailureAsync(
            accepted with { RateBuckets = [] },
            PrivateProbeFailureKind.NoCompatibleTraffic);
    }

    [TestMethod]
    public async Task RejectsNegativeReportDomainsAsMalformed()
    {
        var accepted = CreateAcceptedReport();
        ProbeReport[] malformed =
        [
            accepted with { DurationMilliseconds = -1 },
            accepted with
            {
                Source = accepted.Source with { SocketErrors = -1 },
            },
            accepted with
            {
                Sequence = accepted.Sequence with { Gaps = -1 },
            },
            accepted with
            {
                Headers = accepted.Headers with
                {
                    SessionUidCardinality = -1,
                },
            },
            accepted with
            {
                Headers = accepted.Headers with
                {
                    FrameSkippedIdentifierValues = -1,
                },
            },
            accepted with
            {
                PlayerIndices = accepted.PlayerIndices with
                {
                    SecondaryAbsentCount = -1,
                },
            },
        ];

        foreach (var report in malformed)
        {
            await AssertFailureAsync(
                report,
                PrivateProbeFailureKind.Malformed);
        }
    }

    [TestMethod]
    public async Task SanitizesMalformedUnsafeAndOversizedReportFailures()
    {
        var acceptedJson = ProbeJson.Serialize(CreateAcceptedReport());
        var finalBrace = acceptedJson.LastIndexOf('}');
        var unknownPropertyJson = acceptedJson.Insert(
            finalBrace,
            ",\"unexpectedPrivateSentinel\":1");
        string[] malformedInputs =
        [
            "{",
            unknownPropertyJson,
            new('x', (1024 * 1024) + 1),
        ];

        foreach (var json in malformedInputs)
        {
            using var reportFile = TemporaryReport.Create(json);
            await AssertSanitizedFileFailureAsync(reportFile.Path);
        }

        await AssertSanitizedFileFailureAsync(
            "relative-private-probe.json");
    }

    [TestMethod]
    public async Task RejectsNullRequiredReportMembersAsMalformed()
    {
        var accepted = CreateAcceptedReport();
        ProbeReport[] malformed =
        [
            accepted with { Status = null! },
            accepted with { ProtocolId = null! },
            accepted with { Source = null! },
            accepted with { Classification = null! },
            accepted with { PacketShapes = null! },
            accepted with { Descriptors = null! },
            accepted with { RateBuckets = null! },
            accepted with { Sequence = null! },
            accepted with { Headers = null! },
            accepted with { PlayerIndices = null! },
        ];

        foreach (var report in malformed)
        {
            await AssertFailureAsync(
                report,
                PrivateProbeFailureKind.Malformed);
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private async Task AssertFailureAsync(
        ProbeReport report,
        PrivateProbeFailureKind expected)
    {
        using var reportFile = TemporaryReport.Create(
            ProbeJson.Serialize(report));

        var exception = await Assert.ThrowsAsync<PrivateProbeFailureException>(
            () => PrivateProbeEvidence.ReadAndEvaluateAsync(
                reportFile.Path,
                TestContext.CancellationToken));

        Assert.AreEqual(expected, exception.Kind);
    }

    private async Task AssertSanitizedFileFailureAsync(string path)
    {
        var exception = await Assert.ThrowsAsync<PrivateProbeFailureException>(
            () => PrivateProbeEvidence.ReadAndEvaluateAsync(
                path,
                TestContext.CancellationToken));

        Assert.AreEqual(PrivateProbeFailureKind.Malformed, exception.Kind);
        Assert.DoesNotContain(
            path,
            exception.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "unexpectedPrivateSentinel",
            exception.ToString(),
            StringComparison.Ordinal);
    }

    private static ProbeReport WithClassifierError(
        ProbeReport report,
        ProbeClassificationReport classification) =>
        report with { Classification = classification };

    private static ProbeReport CreateAcceptedReport() =>
        new(
            ProbeAggregator.SchemaVersion,
            "success",
            F125Protocol.Id,
            DurationMilliseconds: 30_000,
            new ProbeSourceReport(
                DatagramsObserved: 4,
                SourceEnqueued: 4,
                SourceDroppedFull: 0,
                SourceRejectedOversized: 0,
                SocketErrors: 0),
            new ProbeClassificationReport(
                SourceDequeued: 4,
                Compatible: 3,
                MalformedHeader: 0,
                UnsupportedFormat: 0,
                UnsupportedYear: 0,
                UnknownPacketId: 0,
                UnsupportedPacketVersion: 0,
                InvalidPacketLength: 0,
                ExcludedPrivacyPacket: 1,
                UnexpectedSender: 0,
                ClassifierAbandonedOnTermination: 0),
            [
                new ProbePacketShapeReport(0, 1, 1_349, 3),
                new ProbePacketShapeReport(4, 1, 1_284, 1),
            ],
            [
                new ProbeDescriptorReport(0, 1, 1_349, 3),
                new ProbeDescriptorReport(4, 1, 1_284, 1),
            ],
            [
                new ProbeRateBucketReport(0, 1),
                new ProbeRateBucketReport(1, 3),
            ],
            new ProbeSequenceReport(
                Gaps: 0,
                Regressions: 0,
                MonotonicTimestampRegressions: 0,
                UtcTimestampRegressions: 0),
            new ProbeHeaderReport(
                SessionUidCardinality: 1,
                SessionTimeRegressions: 0,
                FrameSkippedIdentifierValues: 0,
                FrameRegressions: 0,
                OverallFrameSkippedIdentifierValues: 0,
                OverallFrameRegressions: 0),
            new ProbePlayerIndexReport(
                PlayerMinimum: 0,
                PlayerMaximum: 21,
                SecondaryMinimum: null,
                SecondaryMaximum: null,
                SecondaryAbsentCount: 4));

    private sealed class TemporaryReport : IDisposable
    {
        private TemporaryReport(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryReport Create(string json)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"apexlab-private-probe-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return new TemporaryReport(path);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
