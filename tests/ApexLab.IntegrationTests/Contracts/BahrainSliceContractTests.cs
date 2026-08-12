using System.Text.Json;
using ApexLab.Application.Canonical;
using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;
using ApexLab.Protocols.F125.Canonical;
using ApexLab.Protocols.F125.Decoding;
using ApexLab.Replay.LapAudit;
using ApexLab.Telemetry.Abstractions.Canonical;
using ApexLab.Telemetry.Abstractions.Protocol;

namespace ApexLab.IntegrationTests.Contracts;

[TestClass]
public sealed class BahrainSliceContractTests
{
    private static readonly string[] ExpectedVersionProperties =
    [
        "contractId",
        "decoderId",
        "canonicalSchemaId",
        "lapAuditId",
        "alignmentId",
        "referenceId",
        "cornerRevisionId",
        "detectorId",
        "experimentProtocolId",
        "safeSummarySchemaId",
    ];

    private static readonly string[] ExpectedSafeProperties =
    [
        "schemaVersion",
        "schemaId",
        "status",
        "validationDate",
        "gameBuild",
        "applicationVersion",
        "adapterId",
        "contractId",
        "decoderId",
        "canonicalSchemaId",
        "lapAuditId",
        "alignmentId",
        "referenceId",
        "cornerRevisionId",
        "detectorId",
        "experimentProtocolId",
        "baselineMinimumMet",
        "findingDisposition",
        "experimentOutcome",
        "deterministicReplay",
        "provenanceComplete",
        "privateDataExcluded",
        "conclusion",
    ];

    [TestMethod]
    public void Contract_freezes_authoritative_sources_context_and_versions()
    {
        using var document = LoadJson("contracts/v0.3/bahrain-slice-v1.json");
        var root = document.RootElement;

        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("apexlab-bahrain-tt-slice-v1", root.GetProperty("contractId").GetString());
        Assert.AreEqual("frozen", root.GetProperty("status").GetString());

        var source = root.GetProperty("protocolSource");
        Assert.AreEqual("ea-f1-25-v3", source.GetProperty("adapterId").GetString());
        Assert.AreEqual(2025, source.GetProperty("packetFormat").GetInt32());
        Assert.AreEqual(25, source.GetProperty("gameYear").GetInt32());
        Assert.AreEqual(
            "https://forums.ea.com/blog/f1-games-game-info-hub-en/ea-sports%E2%84%A2-f1%C2%AE25-2026-season-pack-udp-specification/12187347",
            source.GetProperty("officialPage").GetString());

        var attachments = source.GetProperty("attachments").EnumerateArray().ToArray();
        Assert.HasCount(2, attachments);
        AssertAttachment(
            attachments[0],
            "Data Output from F1 25 v3.pdf",
            659792,
            "850199D1EA817B887B150118095C5CA86527A397D578D53FD5C83427636B92D5");
        AssertAttachment(
            attachments[1],
            "F1 25 Telemetry Output Structures.txt",
            51656,
            "49CCEADC60A1A6403A59620C6FC09DA377048BA004B15C199399F775B9B3FC69");

        var context = root.GetProperty("supportedContext");
        Assert.AreEqual(3, context.GetProperty("trackId").GetInt32());
        Assert.AreEqual("Sakhir (Bahrain)", context.GetProperty("trackName").GetString());
        Assert.AreEqual(5, context.GetProperty("gameMode").GetInt32());
        Assert.AreEqual(18, context.GetProperty("sessionType").GetInt32());
        Assert.AreEqual(2, context.GetProperty("ruleSet").GetInt32());
        Assert.AreEqual(0, context.GetProperty("networkGame").GetInt32());
        Assert.AreEqual(0, context.GetProperty("minimumPlayerCarIndex").GetInt32());
        Assert.AreEqual(21, context.GetProperty("maximumPlayerCarIndex").GetInt32());
        Assert.AreEqual(255, context.GetProperty("secondaryPlayerCarIndex").GetInt32());
        Assert.IsFalse(context.GetProperty("spectatingAllowed").GetBoolean());
        Assert.IsTrue(context.GetProperty("stablePlayerCarIndexRequired").GetBoolean());

        var versions = root.GetProperty("versionIds");
        CollectionAssert.AreEquivalent(
            ExpectedVersionProperties,
            versions.EnumerateObject().Select(property => property.Name).ToArray());
        AssertVersion(versions, "contractId", "apexlab-bahrain-tt-slice-v1");
        AssertVersion(versions, "decoderId", "f125-v3-minimal-decoder-v1");
        AssertVersion(versions, "canonicalSchemaId", "apexlab-canonical-sample-v1");
        AssertVersion(versions, "lapAuditId", "bahrain-tt-lap-audit-v1");
        AssertVersion(versions, "alignmentId", "distance-grid-1m-v1");
        AssertVersion(versions, "referenceId", "personal-reference-medoid-v1");
        AssertVersion(versions, "cornerRevisionId", "bahrain-corner-revision-v1");
        AssertVersion(versions, "detectorId", "brake-onset-detector-v1");
        AssertVersion(versions, "experimentProtocolId", "single-braking-experiment-v1");
        AssertVersion(versions, "safeSummarySchemaId", "bahrain-safe-summary-v1");
        Assert.AreEqual(
            versions.GetProperty("decoderId").GetString(),
            F125BahrainPacketDecoder.DecoderId);
    }

    [TestMethod]
    public void Selected_fields_are_unique_bounded_and_owned_by_downstream_issues()
    {
        using var document = LoadJson("contracts/v0.3/bahrain-slice-v1.json");
        var root = document.RootElement;

        var header = root.GetProperty("header");
        Assert.AreEqual(29, header.GetProperty("length").GetInt32());
        var headerFields = header.GetProperty("fields").EnumerateArray().ToArray();
        AssertUniqueFieldNames(headerFields, "Header");
        CollectionAssert.AreEqual(
            new[]
            {
                "m_packetFormat",
                "m_gameYear",
                "m_gameMajorVersion",
                "m_gameMinorVersion",
                "m_packetVersion",
                "m_packetId",
                "m_sessionUID",
                "m_sessionTime",
                "m_frameIdentifier",
                "m_overallFrameIdentifier",
                "m_playerCarIndex",
                "m_secondaryPlayerCarIndex",
            },
            FieldNames(headerFields));

        var nextHeaderOffset = 0;
        foreach (var field in headerFields.OrderBy(field => field.GetProperty("absoluteOffset").GetInt32()))
        {
            AssertFieldMetadata(field);
            var offset = field.GetProperty("absoluteOffset").GetInt32();
            var width = field.GetProperty("width").GetInt32();
            Assert.AreEqual(nextHeaderOffset, offset, field.GetProperty("wireName").GetString());
            nextHeaderOffset += width;
        }

        Assert.AreEqual(29, nextHeaderOffset);

        var packets = root.GetProperty("packets").EnumerateArray().ToArray();
        int[] expectedIds = [0, 1, 2, 3, 6];
        int[] expectedLengths = [1349, 753, 1285, 45, 1352];
        CollectionAssert.AreEqual(expectedIds, packets.Select(PacketId).ToArray());
        CollectionAssert.AreEqual(expectedLengths, packets.Select(PacketLength).ToArray());

        foreach (var packet in packets)
        {
            Assert.AreEqual(1, packet.GetProperty("packetVersion").GetInt32());
            var datagramBytes = PacketLength(packet);
            var layout = packet.GetProperty("layout");
            var kind = layout.GetProperty("kind").GetString();
            var fields = packet.GetProperty("fields").EnumerateArray().ToArray();
            AssertUniqueFieldNames(fields, packet.GetProperty("family").GetString()!);

            foreach (var field in fields)
            {
                AssertFieldMetadata(field);
                var width = field.GetProperty("width").GetInt32();
                int finalByte;
                if (string.Equals(kind, "player-array", StringComparison.Ordinal))
                {
                    var arrayBase = layout.GetProperty("arrayBaseOffset").GetInt32();
                    var arrayCount = layout.GetProperty("arrayCount").GetInt32();
                    var stride = layout.GetProperty("stride").GetInt32();
                    var memberOffset = field.GetProperty("memberOffset").GetInt32();
                    Assert.AreEqual(22, arrayCount);
                    Assert.IsTrue(memberOffset >= 0 && memberOffset + width <= stride);
                    finalByte = arrayBase + ((arrayCount - 1) * stride) + memberOffset + width;
                }
                else
                {
                    finalByte = field.GetProperty("absoluteOffset").GetInt32() + width;
                }

                Assert.IsLessThanOrEqualTo(
                    datagramBytes,
                    finalByte,
                    $"{packet.GetProperty("family").GetString()}:{field.GetProperty("wireName").GetString()} ends at {finalByte}, beyond {datagramBytes}.");
            }
        }

        Assert.AreEqual(60, FindPacket(packets, 0).GetProperty("layout").GetProperty("stride").GetInt32());
        Assert.AreEqual(57, FindPacket(packets, 2).GetProperty("layout").GetProperty("stride").GetInt32());
        Assert.AreEqual(60, FindPacket(packets, 6).GetProperty("layout").GetProperty("stride").GetInt32());

        AssertPacketFieldSet(
            FindPacket(packets, 0),
            "memberOffset",
            ["m_worldPositionX", "m_worldPositionY", "m_worldPositionZ"],
            [0, 4, 8]);
        AssertPacketFieldSet(
            FindPacket(packets, 1),
            "absoluteOffset",
            [
                "m_weather",
                "m_trackTemperature",
                "m_airTemperature",
                "m_trackLength",
                "m_sessionType",
                "m_trackId",
                "m_formula",
                "m_isSpectating",
                "m_networkGame",
                "m_steeringAssist",
                "m_brakingAssist",
                "m_gearboxAssist",
                "m_pitAssist",
                "m_pitReleaseAssist",
                "m_ERSAssist",
                "m_DRSAssist",
                "m_dynamicRacingLine",
                "m_dynamicRacingLineType",
                "m_gameMode",
                "m_ruleSet",
                "m_timeOfDay",
                "m_equalCarPerformance",
                "m_recoveryMode",
            ],
            [29, 30, 31, 33, 35, 36, 37, 44, 154, 685, 686, 687, 688, 689, 690, 691, 692, 693, 694, 695, 696, 708, 709]);
        AssertPacketFieldSet(
            FindPacket(packets, 2),
            "memberOffset",
            [
                "m_lastLapTimeInMS",
                "m_currentLapTimeInMS",
                "m_lapDistance",
                "m_totalDistance",
                "m_currentLapNum",
                "m_pitStatus",
                "m_sector",
                "m_currentLapInvalid",
                "m_driverStatus",
                "m_resultStatus",
            ],
            [0, 4, 20, 24, 33, 34, 36, 37, 44, 45]);
        AssertPacketFieldSet(
            FindPacket(packets, 3),
            "absoluteOffset",
            ["m_eventStringCode", "flashbackFrameIdentifier", "flashbackSessionTime"],
            [29, 33, 37]);
        AssertPacketFieldSet(
            FindPacket(packets, 6),
            "memberOffset",
            ["m_speed", "m_throttle", "m_brake", "m_gear"],
            [0, 2, 10, 15]);

        var manualInputs = root.GetProperty("manualAuditInputs").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "evidenceIdentity",
                "evidenceIntegrityPassed",
                "gameBuild",
                "trackAndModeVisuallyConfirmed",
                "playerVehicle",
                "controllerProfile",
                "setupDescriptor",
                "setupUnchanged",
                "tyreCompound",
                "contextCrossCheckPassed",
                "lapAuditDecision",
                "lapExclusionReason",
                "cornerRevisionId",
                "landmarkReviewId",
            },
            manualInputs.Select(item => item.GetProperty("name").GetString()).ToArray());
        foreach (var input in manualInputs)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(input.GetProperty("scope").GetString()));
            Assert.IsFalse(string.IsNullOrWhiteSpace(input.GetProperty("valueType").GetString()));
            Assert.AreEqual("local-only", input.GetProperty("privacy").GetString());
            Assert.IsNotEmpty(input.GetProperty("consumers").EnumerateArray().ToArray());
            Assert.IsTrue(
                input.TryGetProperty("required", out _) || input.TryGetProperty("requiredWhen", out _),
                input.GetProperty("name").GetString());
        }

        var traceability = root.GetProperty("downstreamTraceability").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(25, 10).ToArray(),
            traceability.Select(item => item.GetProperty("issue").GetInt32()).ToArray());
        foreach (var item in traceability)
        {
            Assert.IsNotEmpty(item.GetProperty("requirements").EnumerateArray().ToArray());
        }

        Assert.AreEqual("local-only", root.GetProperty("privacy").GetProperty("rawEvidenceLocation").GetString());
        Assert.HasCount(3, root.GetProperty("nonClaims").EnumerateArray().ToArray());
    }

    [TestMethod]
    public void Safe_summary_schema_is_closed_complete_and_private()
    {
        using var document = LoadJson("contracts/v0.3/bahrain-safe-summary-v1.schema.json");
        var root = document.RootElement;

        Assert.AreEqual("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
        Assert.AreEqual("https://apexlab.local/contracts/v0.3/bahrain-safe-summary-v1.schema.json", root.GetProperty("$id").GetString());
        Assert.IsFalse(root.GetProperty("additionalProperties").GetBoolean());

        var required = root.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!)
            .ToArray();
        var properties = root.GetProperty("properties");
        var propertyNames = properties.EnumerateObject().Select(property => property.Name).ToArray();
        CollectionAssert.AreEquivalent(ExpectedSafeProperties, required);
        CollectionAssert.AreEquivalent(ExpectedSafeProperties, propertyNames);

        AssertSchemaConst(properties, "schemaVersion", "1");
        AssertSchemaConst(properties, "schemaId", "bahrain-safe-summary-v1");
        AssertSchemaConst(properties, "adapterId", "ea-f1-25-v3");
        AssertSchemaConst(properties, "contractId", "apexlab-bahrain-tt-slice-v1");
        AssertSchemaConst(properties, "decoderId", "f125-v3-minimal-decoder-v1");
        AssertSchemaConst(properties, "canonicalSchemaId", "apexlab-canonical-sample-v1");
        AssertSchemaConst(properties, "lapAuditId", "bahrain-tt-lap-audit-v1");
        AssertSchemaConst(properties, "alignmentId", "distance-grid-1m-v1");
        AssertSchemaConst(properties, "referenceId", "personal-reference-medoid-v1");
        AssertSchemaConst(properties, "cornerRevisionId", "bahrain-corner-revision-v1");
        AssertSchemaConst(properties, "detectorId", "brake-onset-detector-v1");
        AssertSchemaConst(properties, "experimentProtocolId", "single-braking-experiment-v1");
        Assert.IsGreaterThanOrEqualTo(2, root.GetProperty("allOf").GetArrayLength());

        string[] forbidden =
        [
            "captureId",
            "manifestHash",
            "sha256",
            "sessionUid",
            "frameIdentifier",
            "sender",
            "path",
            "rate",
            "lapTime",
            "segmentTime",
            "coordinates",
            "setup",
            "notes",
            "sourceChunkHash",
            "payload",
        ];
        var names = CollectPropertyNames(root).ToHashSet(StringComparer.Ordinal);
        foreach (var forbiddenName in forbidden)
        {
            Assert.DoesNotContain(forbiddenName, names, forbiddenName);
        }
    }

    [TestMethod]
    public void Production_decoder_surface_preserves_units_and_excludes_transport_data()
    {
        AssertProperties<F125MotionPlayerData>(
            ("Header", typeof(TelemetryHeaderMetadata)),
            ("WorldPositionXMetres", typeof(float)),
            ("WorldPositionYMetres", typeof(float)),
            ("WorldPositionZMetres", typeof(float)));
        AssertProperties<F125SessionData>(
            ("Header", typeof(TelemetryHeaderMetadata)),
            ("Weather", typeof(byte)),
            ("TrackTemperatureCelsius", typeof(sbyte)),
            ("AirTemperatureCelsius", typeof(sbyte)),
            ("TrackLengthMetres", typeof(ushort)),
            ("SessionType", typeof(byte)),
            ("TrackId", typeof(sbyte)),
            ("Formula", typeof(byte)),
            ("IsSpectating", typeof(bool)),
            ("IsNetworkGame", typeof(bool)),
            ("SteeringAssist", typeof(byte)),
            ("BrakingAssist", typeof(byte)),
            ("GearboxAssist", typeof(byte)),
            ("PitAssist", typeof(byte)),
            ("PitReleaseAssist", typeof(byte)),
            ("ErsAssist", typeof(byte)),
            ("DrsAssist", typeof(byte)),
            ("DynamicRacingLine", typeof(byte)),
            ("DynamicRacingLineType", typeof(byte)),
            ("GameMode", typeof(byte)),
            ("RuleSet", typeof(byte)),
            ("TimeOfDayMinutesSinceMidnight", typeof(uint)),
            ("EqualCarPerformance", typeof(bool)),
            ("RecoveryMode", typeof(byte)));
        AssertProperties<F125LapPlayerData>(
            ("Header", typeof(TelemetryHeaderMetadata)),
            ("LastLapTimeMilliseconds", typeof(uint)),
            ("CurrentLapTimeMilliseconds", typeof(uint)),
            ("LapDistanceMetres", typeof(float)),
            ("TotalDistanceMetres", typeof(float)),
            ("CurrentLapNumber", typeof(byte)),
            ("PitStatus", typeof(byte)),
            ("Sector", typeof(byte)),
            ("CurrentLapInvalid", typeof(bool)),
            ("DriverStatus", typeof(byte)),
            ("ResultStatus", typeof(byte)));
        AssertProperties<F125EventData>(
            ("Header", typeof(TelemetryHeaderMetadata)),
            ("Kind", typeof(F125SliceEventKind)),
            ("FlashbackFrameIdentifier", typeof(uint?)),
            ("FlashbackSessionTimeSeconds", typeof(float?)));
        AssertProperties<F125CarTelemetryPlayerData>(
            ("Header", typeof(TelemetryHeaderMetadata)),
            ("SpeedKilometresPerHour", typeof(ushort)),
            ("ThrottleRatio", typeof(float)),
            ("BrakeRatio", typeof(float)),
            ("Gear", typeof(sbyte)));
    }

    [TestMethod]
    public void Production_canonical_replay_ids_and_surface_match_the_frozen_contract()
    {
        using var document = LoadJson("contracts/v0.3/bahrain-slice-v1.json");
        var root = document.RootElement;
        var versions = root.GetProperty("versionIds");
        var projector = new F125BahrainCanonicalProjector();

        Assert.AreEqual(
            root.GetProperty("protocolSource").GetProperty("adapterId").GetString(),
            projector.ProtocolId);
        Assert.AreEqual(
            versions.GetProperty("contractId").GetString(),
            projector.ContractId);
        Assert.AreEqual(
            versions.GetProperty("decoderId").GetString(),
            projector.DecoderId);
        Assert.AreEqual(
            versions.GetProperty("canonicalSchemaId").GetString(),
            projector.CanonicalSchemaId);

        var identity = new CanonicalReplayIdentity(
            new string('a', 64),
            projector.ProtocolId,
            projector.ContractId,
            projector.DecoderId,
            projector.CanonicalSchemaId);
        Assert.AreEqual(projector.ProtocolId, identity.ProtocolId);
        Assert.AreEqual(projector.ContractId, identity.ContractId);
        Assert.AreEqual(projector.DecoderId, identity.DecoderId);
        Assert.AreEqual(projector.CanonicalSchemaId, identity.CanonicalSchemaId);

        AssertProperties<CanonicalPacketHeader>(
            ("SessionTimeSeconds", typeof(float)),
            ("FrameIdentifier", typeof(uint)),
            ("OverallFrameIdentifier", typeof(uint)),
            ("PlayerCarIndex", typeof(byte)),
            ("SecondaryPlayerCarIndex", typeof(byte)));
        AssertProperties<CanonicalMotionPacket>(
            ("WorldPositionXMetres", typeof(float)),
            ("WorldPositionYMetres", typeof(float)),
            ("WorldPositionZMetres", typeof(float)));
        AssertProperties<CanonicalSessionPacket>(
            ("Weather", typeof(byte)),
            ("TrackTemperatureCelsius", typeof(sbyte)),
            ("AirTemperatureCelsius", typeof(sbyte)),
            ("TrackLengthMetres", typeof(ushort)),
            ("SessionType", typeof(byte)),
            ("TrackId", typeof(sbyte)),
            ("Formula", typeof(byte)),
            ("IsSpectating", typeof(bool)),
            ("IsNetworkGame", typeof(bool)),
            ("SteeringAssist", typeof(byte)),
            ("BrakingAssist", typeof(byte)),
            ("GearboxAssist", typeof(byte)),
            ("PitAssist", typeof(byte)),
            ("PitReleaseAssist", typeof(byte)),
            ("ErsAssist", typeof(byte)),
            ("DrsAssist", typeof(byte)),
            ("DynamicRacingLine", typeof(byte)),
            ("DynamicRacingLineType", typeof(byte)),
            ("GameMode", typeof(byte)),
            ("RuleSet", typeof(byte)),
            ("TimeOfDayMinutesSinceMidnight", typeof(uint)),
            ("EqualCarPerformance", typeof(bool)),
            ("RecoveryMode", typeof(byte)));
        AssertProperties<CanonicalLapPacket>(
            ("LastLapTimeMilliseconds", typeof(uint)),
            ("CurrentLapTimeMilliseconds", typeof(uint)),
            ("LapDistanceMetres", typeof(float)),
            ("TotalDistanceMetres", typeof(float)),
            ("CurrentLapNumber", typeof(byte)),
            ("PitStatus", typeof(byte)),
            ("Sector", typeof(byte)),
            ("CurrentLapInvalid", typeof(bool)),
            ("DriverStatus", typeof(byte)),
            ("ResultStatus", typeof(byte)));
        AssertProperties<CanonicalEventPacket>(
            ("Kind", typeof(CanonicalEventKind)),
            ("FlashbackFrameIdentifier", typeof(uint?)),
            ("FlashbackSessionTimeSeconds", typeof(float?)));
        AssertProperties<CanonicalCarTelemetryPacket>(
            ("SpeedKilometresPerHour", typeof(ushort)),
            ("ThrottleRatio", typeof(float)),
            ("BrakeRatio", typeof(float)),
            ("Gear", typeof(sbyte)));
        AssertProperties<CanonicalPacket>(
            ("Family", typeof(CanonicalPacketFamily)),
            ("Header", typeof(CanonicalPacketHeader)),
            ("Motion", typeof(CanonicalMotionPacket?)),
            ("Session", typeof(CanonicalSessionPacket?)),
            ("Lap", typeof(CanonicalLapPacket?)),
            ("Event", typeof(CanonicalEventPacket?)),
            ("CarTelemetry", typeof(CanonicalCarTelemetryPacket?)));
    }

    [TestMethod]
    public void Production_lap_audit_surface_matches_the_frozen_contract()
    {
        using var document = LoadJson("contracts/v0.3/bahrain-slice-v1.json");
        var root = document.RootElement;
        var versions = root.GetProperty("versionIds");
        var supported = root.GetProperty("supportedContext");

        Assert.AreEqual(
            BahrainLapAuditContract.AuditId,
            versions.GetProperty("lapAuditId").GetString());
        Assert.AreEqual(
            5,
            typeof(BahrainLapAuditContract)
                .GetField(nameof(BahrainLapAuditContract.MinimumComparableBaselineLaps))!
                .GetRawConstantValue());
        CollectionAssert.AreEqual(
            new[] { LapAuditDecision.Included, LapAuditDecision.Excluded },
            Enum.GetValues<LapAuditDecision>());
        CollectionAssert.AreEqual(
            new[]
            {
                LapExclusionReason.Invalidated,
                LapExclusionReason.PitEntryOrExit,
                LapExclusionReason.FlashbackObserved,
                LapExclusionReason.MaterialGap,
                LapExclusionReason.ContextMismatch,
                LapExclusionReason.IncompleteLap,
                LapExclusionReason.OtherFactual,
            },
            Enum.GetValues<LapExclusionReason>());

        AssertProperties<BahrainLapAuditManualInputs>(
            ("GameBuild", typeof(string)),
            ("PlayerVehicle", typeof(string)),
            ("ControllerProfile", typeof(string)),
            ("SetupDescriptor", typeof(string)),
            ("TyreCompound", typeof(string)),
            ("EvidenceIntegrityPassed", typeof(bool?)),
            ("TrackAndModeVisuallyConfirmed", typeof(bool?)),
            ("SetupUnchanged", typeof(bool?)),
            ("ContextCrossCheckPassed", typeof(bool?)),
            ("IsComplete", typeof(bool)));
        AssertProperties<LapBoundary>(
            ("Completeness", typeof(LapBoundaryCompleteness)),
            ("StartSourceSequence", typeof(long)),
            ("EndSourceSequenceExclusive", typeof(long)),
            ("CompletionEvidenceSourceSequence", typeof(long?)),
            ("LapNumber", typeof(byte?)),
            ("OfficialLapTimeMilliseconds", typeof(uint?)));
        AssertProperties<BahrainLapAuditValidationReport>(
            ("SchemaVersion", typeof(int)),
            ("SchemaId", typeof(string)),
            ("Status", typeof(string)),
            ("LapAuditId", typeof(string)),
            ("IncludedCount", typeof(int)),
            ("ExcludedCount", typeof(int)),
            ("PendingCount", typeof(int)),
            ("MinimumRequiredCount", typeof(int)),
            ("AllCandidatesAudited", typeof(bool)),
            ("IncludedContextsMatch", typeof(bool)),
            ("ProvenanceComplete", typeof(bool)),
            ("DeterministicSelection", typeof(bool)),
            ("PrivateDataExcluded", typeof(bool)),
            ("Conclusion", typeof(string)));
        AssertProperties<BahrainLapAuditPrepareSuccessReport>(
            ("SchemaVersion", typeof(int)),
            ("SchemaId", typeof(string)),
            ("Status", typeof(string)),
            ("LapAuditId", typeof(string)),
            ("CandidateCount", typeof(int)),
            ("PrivateDataExcluded", typeof(bool)),
            ("NextAction", typeof(string)));

        var context = new BahrainTelemetryContext(
            new CanonicalSessionPacket(
                weather: 0,
                trackTemperatureCelsius: 30,
                airTemperatureCelsius: 20,
                trackLengthMetres: 5_412,
                sessionType: checked((byte)supported.GetProperty("sessionType").GetInt32()),
                trackId: checked((sbyte)supported.GetProperty("trackId").GetInt32()),
                formula: 0,
                isSpectating: false,
                isNetworkGame: false,
                steeringAssist: 0,
                brakingAssist: 0,
                gearboxAssist: 1,
                pitAssist: 0,
                pitReleaseAssist: 0,
                ersAssist: 0,
                drsAssist: 0,
                dynamicRacingLine: 0,
                dynamicRacingLineType: 0,
                gameMode: checked((byte)supported.GetProperty("gameMode").GetInt32()),
                ruleSet: checked((byte)supported.GetProperty("ruleSet").GetInt32()),
                timeOfDayMinutesSinceMidnight: 720,
                equalCarPerformance: true,
                recoveryMode: 0),
            playerCarIndex: checked((byte)supported.GetProperty("minimumPlayerCarIndex").GetInt32()),
            secondaryPlayerCarIndex: checked((byte)supported.GetProperty("secondaryPlayerCarIndex").GetInt32()));
        Assert.IsTrue(context.IsSupported);
        Assert.AreEqual(3, context.TrackId);
        Assert.AreEqual(5, context.GameMode);
        Assert.AreEqual(18, context.SessionType);
        Assert.AreEqual(2, context.RuleSet);
    }

    private static void AssertAttachment(JsonElement attachment, string name, int bytes, string sha256)
    {
        Assert.AreEqual(name, attachment.GetProperty("name").GetString());
        Assert.AreEqual(bytes, attachment.GetProperty("bytes").GetInt32());
        Assert.AreEqual(sha256, attachment.GetProperty("sha256").GetString());
    }

    private static void AssertProperties<T>(
        params (string Name, Type Type)[] expected)
    {
        var actual = typeof(T)
            .GetProperties()
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => (property.Name, property.PropertyType))
            .ToArray();
        var orderedExpected = expected
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(orderedExpected, actual);

        string[] forbidden =
        [
            "Payload",
            "Datagram",
            "Sender",
            "Path",
            "CaptureId",
            "Sha256",
            "SessionUid",
            "ReceivedAtUtc",
            "MonotonicTimestamp",
            "DataLeafName",
            "DataSha256",
            "CanonicalSha256",
        ];
        var names = actual.Select(property => property.Name).ToArray();
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, names);
        }
    }

    private static void AssertVersion(JsonElement versions, string propertyName, string expected)
    {
        Assert.AreEqual(expected, versions.GetProperty(propertyName).GetString());
    }

    private static void AssertUniqueFieldNames(IEnumerable<JsonElement> fields, string scope)
    {
        var names = fields.Select(field => field.GetProperty("wireName").GetString()!).ToArray();
        Assert.AreEqual(names.Length, names.Distinct(StringComparer.Ordinal).Count(), scope);
    }

    private static string?[] FieldNames(IEnumerable<JsonElement> fields)
    {
        return fields.Select(field => field.GetProperty("wireName").GetString()).ToArray();
    }

    private static void AssertPacketFieldSet(
        JsonElement packet,
        string offsetProperty,
        string[] expectedNames,
        int[] expectedOffsets)
    {
        var fields = packet.GetProperty("fields").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(expectedNames, FieldNames(fields));
        CollectionAssert.AreEqual(
            expectedOffsets,
            fields.Select(field => field.GetProperty(offsetProperty).GetInt32()).ToArray());
    }

    private static void AssertFieldMetadata(JsonElement field)
    {
        var wireName = field.GetProperty("wireName").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(wireName));
        Assert.IsGreaterThan(0, field.GetProperty("width").GetInt32(), wireName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(field.GetProperty("wireType").GetString()), wireName);
        Assert.IsFalse(string.IsNullOrWhiteSpace(field.GetProperty("unit").GetString()), wireName);

        var consumers = field.GetProperty("consumers").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        Assert.IsNotEmpty(consumers, wireName);
        Assert.IsTrue(consumers.All(issue => issue is >= 25 and <= 34), wireName);
    }

    private static int PacketId(JsonElement packet) => packet.GetProperty("packetId").GetInt32();

    private static int PacketLength(JsonElement packet) => packet.GetProperty("datagramBytes").GetInt32();

    private static JsonElement FindPacket(IEnumerable<JsonElement> packets, int packetId)
    {
        return packets.Single(packet => PacketId(packet) == packetId);
    }

    private static void AssertSchemaConst(JsonElement properties, string propertyName, string expected)
    {
        var constant = properties.GetProperty(propertyName).GetProperty("const");
        Assert.AreEqual(expected, constant.ValueKind == JsonValueKind.Number ? constant.GetRawText() : constant.GetString());
    }

    private static IEnumerable<string> CollectPropertyNames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var descendant in CollectPropertyNames(property.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in CollectPropertyNames(item))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static JsonDocument LoadJson(string relativePath)
    {
        var path = Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        return JsonDocument.Parse(
            File.ReadAllBytes(path),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ApexLab.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root containing ApexLab.slnx.");
    }
}
