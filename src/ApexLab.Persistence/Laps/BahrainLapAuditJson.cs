using System.Text.Json;
using ApexLab.Application.Laps;
using ApexLab.Domain.Laps;
using ApexLab.Telemetry.Abstractions.Canonical;

namespace ApexLab.Persistence.Laps;

internal static class BahrainLapAuditJson
{
    private static readonly string[] RootProperties =
    [
        "schemaVersion", "lapAuditId", "canonicalIdentitySha256",
        "canonicalSha256", "referenceContext", "manualInputs", "entries",
    ];

    private static readonly string[] ContextProperties =
    [
        "weather", "trackTemperatureCelsius", "airTemperatureCelsius",
        "trackLengthMetres", "sessionType", "trackId", "formula",
        "isSpectating", "isNetworkGame", "steeringAssist", "brakingAssist",
        "gearboxAssist", "pitAssist", "pitReleaseAssist", "ersAssist",
        "drsAssist", "dynamicRacingLine", "dynamicRacingLineType", "gameMode",
        "ruleSet", "timeOfDayMinutesSinceMidnight", "equalCarPerformance",
        "recoveryMode", "playerCarIndex", "secondaryPlayerCarIndex",
    ];

    private static readonly string[] ManualProperties =
    [
        "gameBuild", "playerVehicle", "controllerProfile", "setupDescriptor",
        "tyreCompound", "evidenceIntegrityPassed",
        "trackAndModeVisuallyConfirmed", "setupUnchanged",
        "contextCrossCheckPassed",
    ];

    private static readonly string[] EntryProperties =
    [
        "candidateId", "boundary", "evidenceFlags", "context", "decision",
        "exclusionReason", "factualNote",
    ];

    private static readonly string[] BoundaryProperties =
    [
        "completeness", "startSourceSequence", "endSourceSequenceExclusive",
        "completionEvidenceSourceSequence", "lapNumber",
        "officialLapTimeMilliseconds",
    ];

    public const int MaximumLengthBytes = 2 * 1_024 * 1_024;

    public static byte[] Serialize(BahrainLapAuditDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", document.SchemaVersion);
            writer.WriteString("lapAuditId", document.LapAuditId);
            writer.WriteString(
                "canonicalIdentitySha256",
                document.CanonicalIdentitySha256);
            writer.WriteString("canonicalSha256", document.CanonicalSha256);
            WriteContext(writer, "referenceContext", document.ReferenceContext);
            WriteManualInputs(writer, document.ManualInputs);
            writer.WriteStartArray("entries");
            foreach (var entry in document.Entries)
            {
                ValidateEntryShape(entry);
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        if (stream.Length > MaximumLengthBytes)
        {
            throw new InvalidOperationException(
                "The lap audit document exceeded its supported bound.");
        }

        return stream.ToArray();
    }

    public static BahrainLapAuditDocument Deserialize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 3 or > MaximumLengthBytes
            || utf8[0] == 0xef
            || utf8[^1] != (byte)'\n'
            || (utf8.Length >= 2 && utf8[^2] == (byte)'\r'))
        {
            throw FormatFailure();
        }

        try
        {
            using var json = JsonDocument.Parse(
                utf8[..^1].ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 8,
                });
            var root = json.RootElement;
            RequireProperties(root, RootProperties);
            var entriesElement = root.GetProperty("entries");
            if (entriesElement.ValueKind != JsonValueKind.Array
                || entriesElement.GetArrayLength()
                > BahrainLapAuditContract.MaximumInventoryCandidates)
            {
                throw FormatFailure();
            }

            var entries = entriesElement.EnumerateArray()
                .Select(ReadEntry)
                .ToArray();
            return new(
                ReadInt32(root, "schemaVersion"),
                ReadRequiredString(root, "lapAuditId", 64),
                ReadRequiredString(root, "canonicalIdentitySha256", 64),
                ReadRequiredString(root, "canonicalSha256", 64),
                ReadNullableContext(root.GetProperty("referenceContext")),
                ReadManualInputs(root.GetProperty("manualInputs")),
                entries);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                   JsonException
                   or ArgumentException
                   or InvalidOperationException
                   or KeyNotFoundException
                   or OverflowException
                   or FormatException)
        {
            throw FormatFailure(exception);
        }
    }

    private static void WriteManualInputs(
        Utf8JsonWriter writer,
        BahrainLapAuditManualInputs inputs)
    {
        ValidateNullableText(inputs.GameBuild, 80);
        ValidateNullableText(inputs.PlayerVehicle, 80);
        ValidateNullableText(inputs.ControllerProfile, 80);
        ValidateNullableText(inputs.SetupDescriptor, 120);
        ValidateNullableText(inputs.TyreCompound, 40);
        writer.WriteStartObject("manualInputs");
        WriteNullableString(writer, "gameBuild", inputs.GameBuild);
        WriteNullableString(writer, "playerVehicle", inputs.PlayerVehicle);
        WriteNullableString(writer, "controllerProfile", inputs.ControllerProfile);
        WriteNullableString(writer, "setupDescriptor", inputs.SetupDescriptor);
        WriteNullableString(writer, "tyreCompound", inputs.TyreCompound);
        WriteNullableBoolean(
            writer,
            "evidenceIntegrityPassed",
            inputs.EvidenceIntegrityPassed);
        WriteNullableBoolean(
            writer,
            "trackAndModeVisuallyConfirmed",
            inputs.TrackAndModeVisuallyConfirmed);
        WriteNullableBoolean(writer, "setupUnchanged", inputs.SetupUnchanged);
        WriteNullableBoolean(
            writer,
            "contextCrossCheckPassed",
            inputs.ContextCrossCheckPassed);
        writer.WriteEndObject();
    }

    private static BahrainLapAuditManualInputs ReadManualInputs(JsonElement element)
    {
        RequireProperties(element, ManualProperties);
        return new(
            ReadNullableString(element, "gameBuild", 80),
            ReadNullableString(element, "playerVehicle", 80),
            ReadNullableString(element, "controllerProfile", 80),
            ReadNullableString(element, "setupDescriptor", 120),
            ReadNullableString(element, "tyreCompound", 40),
            ReadNullableBoolean(element, "evidenceIntegrityPassed"),
            ReadNullableBoolean(element, "trackAndModeVisuallyConfirmed"),
            ReadNullableBoolean(element, "setupUnchanged"),
            ReadNullableBoolean(element, "contextCrossCheckPassed"));
    }

    private static void WriteEntry(
        Utf8JsonWriter writer,
        BahrainLapAuditEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("candidateId", entry.CandidateId.Value);
        WriteBoundary(writer, entry.Boundary);
        WriteEvidenceFlags(writer, entry.EvidenceFlags);
        WriteContext(writer, "context", entry.Context);
        WriteNullableString(
            writer,
            "decision",
            entry.Decision switch
            {
                null => null,
                LapAuditDecision.Included => "included",
                LapAuditDecision.Excluded => "excluded",
                _ => throw new ArgumentOutOfRangeException(nameof(entry)),
            });
        WriteNullableString(
            writer,
            "exclusionReason",
            entry.ExclusionReason.HasValue
                ? FormatReason(entry.ExclusionReason.Value)
                : null);
        WriteNullableString(writer, "factualNote", entry.FactualNote);
        writer.WriteEndObject();
    }

    private static BahrainLapAuditEntry ReadEntry(JsonElement element)
    {
        RequireProperties(element, EntryProperties);
        var entry = new BahrainLapAuditEntry(
            new LapCandidateId(ReadRequiredString(element, "candidateId", 64)),
            ReadBoundary(element.GetProperty("boundary")),
            ReadEvidenceFlags(element.GetProperty("evidenceFlags")),
            ReadNullableContext(element.GetProperty("context")),
            ReadDecision(element.GetProperty("decision")),
            ReadReason(element.GetProperty("exclusionReason")),
            ReadNullableString(element, "factualNote", 200));
        ValidateEntryShape(entry);
        return entry;
    }

    private static void WriteBoundary(Utf8JsonWriter writer, LapBoundary boundary)
    {
        writer.WriteStartObject("boundary");
        writer.WriteString(
            "completeness",
            boundary.Completeness switch
            {
                LapBoundaryCompleteness.Complete => "complete",
                LapBoundaryCompleteness.LeadingPartial => "leading-partial",
                LapBoundaryCompleteness.TrailingPartial => "trailing-partial",
                LapBoundaryCompleteness.Incoherent => "incoherent",
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            });
        writer.WriteNumber("startSourceSequence", boundary.StartSourceSequence);
        writer.WriteNumber(
            "endSourceSequenceExclusive",
            boundary.EndSourceSequenceExclusive);
        WriteNullableInt64(
            writer,
            "completionEvidenceSourceSequence",
            boundary.CompletionEvidenceSourceSequence);
        WriteNullableByte(writer, "lapNumber", boundary.LapNumber);
        WriteNullableUInt32(
            writer,
            "officialLapTimeMilliseconds",
            boundary.OfficialLapTimeMilliseconds);
        writer.WriteEndObject();
    }

    private static LapBoundary ReadBoundary(JsonElement element)
    {
        RequireProperties(element, BoundaryProperties);
        var completeness = ReadRequiredString(element, "completeness", 32) switch
        {
            "complete" => LapBoundaryCompleteness.Complete,
            "leading-partial" => LapBoundaryCompleteness.LeadingPartial,
            "trailing-partial" => LapBoundaryCompleteness.TrailingPartial,
            "incoherent" => LapBoundaryCompleteness.Incoherent,
            _ => throw FormatFailure(),
        };
        var start = ReadInt64(element, "startSourceSequence");
        var end = ReadInt64(element, "endSourceSequenceExclusive");
        var completion = ReadNullableInt64(
            element,
            "completionEvidenceSourceSequence");
        var lapNumber = ReadNullableByte(element, "lapNumber");
        var officialTime = ReadNullableUInt32(
            element,
            "officialLapTimeMilliseconds");
        if (completeness == LapBoundaryCompleteness.Complete)
        {
            if (!completion.HasValue
                || completion.Value != end
                || !lapNumber.HasValue
                || !officialTime.HasValue)
            {
                throw FormatFailure();
            }

            return LapBoundary.Complete(
                start,
                completion.Value,
                lapNumber.Value,
                officialTime.Value);
        }

        if (completion.HasValue || officialTime.HasValue)
        {
            throw FormatFailure();
        }

        return LapBoundary.Partial(completeness, start, end, lapNumber);
    }

    private static void WriteEvidenceFlags(
        Utf8JsonWriter writer,
        LapEvidenceFlags flags)
    {
        LapEvidence.Validate(flags);
        writer.WriteStartArray("evidenceFlags");
        foreach (var pair in FlagNames)
        {
            if (flags.HasFlag(pair.Flag))
            {
                writer.WriteStringValue(pair.Name);
            }
        }

        writer.WriteEndArray();
    }

    private static LapEvidenceFlags ReadEvidenceFlags(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array
            || element.GetArrayLength() > FlagNames.Length)
        {
            throw FormatFailure();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var flags = LapEvidenceFlags.None;
        foreach (var value in element.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String
                || value.GetString() is not { } name
                || !seen.Add(name))
            {
                throw FormatFailure();
            }

            var match = FlagNames.FirstOrDefault(pair =>
                StringComparer.Ordinal.Equals(pair.Name, name));
            if (match == default)
            {
                throw FormatFailure();
            }

            flags |= match.Flag;
        }

        return LapEvidence.Validate(flags);
    }

    private static void WriteContext(
        Utf8JsonWriter writer,
        string propertyName,
        BahrainTelemetryContext? context)
    {
        if (context is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteStartObject(propertyName);
        writer.WriteNumber("weather", context.Weather);
        writer.WriteNumber("trackTemperatureCelsius", context.TrackTemperatureCelsius);
        writer.WriteNumber("airTemperatureCelsius", context.AirTemperatureCelsius);
        writer.WriteNumber("trackLengthMetres", context.TrackLengthMetres);
        writer.WriteNumber("sessionType", context.SessionType);
        writer.WriteNumber("trackId", context.TrackId);
        writer.WriteNumber("formula", context.Formula);
        writer.WriteBoolean("isSpectating", context.IsSpectating);
        writer.WriteBoolean("isNetworkGame", context.IsNetworkGame);
        writer.WriteNumber("steeringAssist", context.SteeringAssist);
        writer.WriteNumber("brakingAssist", context.BrakingAssist);
        writer.WriteNumber("gearboxAssist", context.GearboxAssist);
        writer.WriteNumber("pitAssist", context.PitAssist);
        writer.WriteNumber("pitReleaseAssist", context.PitReleaseAssist);
        writer.WriteNumber("ersAssist", context.ErsAssist);
        writer.WriteNumber("drsAssist", context.DrsAssist);
        writer.WriteNumber("dynamicRacingLine", context.DynamicRacingLine);
        writer.WriteNumber("dynamicRacingLineType", context.DynamicRacingLineType);
        writer.WriteNumber("gameMode", context.GameMode);
        writer.WriteNumber("ruleSet", context.RuleSet);
        writer.WriteNumber(
            "timeOfDayMinutesSinceMidnight",
            context.TimeOfDayMinutesSinceMidnight);
        writer.WriteBoolean("equalCarPerformance", context.EqualCarPerformance);
        writer.WriteNumber("recoveryMode", context.RecoveryMode);
        writer.WriteNumber("playerCarIndex", context.PlayerCarIndex);
        writer.WriteNumber(
            "secondaryPlayerCarIndex",
            context.SecondaryPlayerCarIndex);
        writer.WriteEndObject();
    }

    private static BahrainTelemetryContext? ReadNullableContext(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        RequireProperties(element, ContextProperties);
        var session = new CanonicalSessionPacket(
            ReadByte(element, "weather"),
            ReadSByte(element, "trackTemperatureCelsius"),
            ReadSByte(element, "airTemperatureCelsius"),
            ReadUInt16(element, "trackLengthMetres"),
            ReadByte(element, "sessionType"),
            ReadSByte(element, "trackId"),
            ReadByte(element, "formula"),
            ReadBoolean(element, "isSpectating"),
            ReadBoolean(element, "isNetworkGame"),
            ReadByte(element, "steeringAssist"),
            ReadByte(element, "brakingAssist"),
            ReadByte(element, "gearboxAssist"),
            ReadByte(element, "pitAssist"),
            ReadByte(element, "pitReleaseAssist"),
            ReadByte(element, "ersAssist"),
            ReadByte(element, "drsAssist"),
            ReadByte(element, "dynamicRacingLine"),
            ReadByte(element, "dynamicRacingLineType"),
            ReadByte(element, "gameMode"),
            ReadByte(element, "ruleSet"),
            ReadUInt32(element, "timeOfDayMinutesSinceMidnight"),
            ReadBoolean(element, "equalCarPerformance"),
            ReadByte(element, "recoveryMode"));
        return new(
            session,
            ReadByte(element, "playerCarIndex"),
            ReadByte(element, "secondaryPlayerCarIndex"));
    }

    private static LapAuditDecision? ReadDecision(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when element.GetString() == "included" =>
                LapAuditDecision.Included,
            JsonValueKind.String when element.GetString() == "excluded" =>
                LapAuditDecision.Excluded,
            _ => throw FormatFailure(),
        };

    private static LapExclusionReason? ReadReason(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw FormatFailure();
        }

        return element.GetString() switch
        {
            "invalidated" => LapExclusionReason.Invalidated,
            "pit-entry-or-exit" => LapExclusionReason.PitEntryOrExit,
            "flashback-observed" => LapExclusionReason.FlashbackObserved,
            "material-gap" => LapExclusionReason.MaterialGap,
            "context-mismatch" => LapExclusionReason.ContextMismatch,
            "incomplete-lap" => LapExclusionReason.IncompleteLap,
            "other-factual" => LapExclusionReason.OtherFactual,
            _ => throw FormatFailure(),
        };
    }

    private static string FormatReason(LapExclusionReason reason) =>
        reason switch
        {
            LapExclusionReason.Invalidated => "invalidated",
            LapExclusionReason.PitEntryOrExit => "pit-entry-or-exit",
            LapExclusionReason.FlashbackObserved => "flashback-observed",
            LapExclusionReason.MaterialGap => "material-gap",
            LapExclusionReason.ContextMismatch => "context-mismatch",
            LapExclusionReason.IncompleteLap => "incomplete-lap",
            LapExclusionReason.OtherFactual => "other-factual",
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };

    private static void ValidateEntryShape(BahrainLapAuditEntry entry)
    {
        if (!entry.Decision.HasValue)
        {
            if (entry.ExclusionReason.HasValue || entry.FactualNote is not null)
            {
                throw new ArgumentException("A pending audit entry cannot have a reason.");
            }

            return;
        }

        if (!Enum.IsDefined(entry.Decision.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(entry));
        }

        if (entry.Decision == LapAuditDecision.Included)
        {
            if (entry.ExclusionReason.HasValue || entry.FactualNote is not null)
            {
                throw new ArgumentException("An included audit entry cannot have a reason.");
            }

            return;
        }

        if (!entry.ExclusionReason.HasValue
            || !Enum.IsDefined(entry.ExclusionReason.Value)
            || (entry.ExclusionReason == LapExclusionReason.OtherFactual)
            != (entry.FactualNote is not null))
        {
            throw new ArgumentException("An excluded audit entry requires its conditional reason.");
        }

        if (entry.FactualNote is not null)
        {
            ValidateText(entry.FactualNote, 200);
        }
    }

    private static void RequireProperties(
        JsonElement element,
        IReadOnlyCollection<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw FormatFailure();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name, StringComparer.Ordinal)
                || !seen.Add(property.Name))
            {
                throw FormatFailure();
            }
        }

        if (seen.Count != expected.Count)
        {
            throw FormatFailure();
        }
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName,
        int maximumLength)
    {
        var value = ReadNullableString(element, propertyName, maximumLength);
        return value ?? throw FormatFailure();
    }

    private static string? ReadNullableString(
        JsonElement element,
        string propertyName,
        int maximumLength)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String
            || property.GetString() is not { } value)
        {
            throw FormatFailure();
        }

        ValidateText(value, maximumLength);
        return value;
    }

    private static void ValidateText(string value, int maximumLength)
    {
        if (value.Length is < 1
            || value.Length > maximumLength
            || !StringComparer.Ordinal.Equals(value, value.Trim())
            || value.Any(character => char.IsControl(character)
                || char.IsSurrogate(character)))
        {
            throw FormatFailure();
        }
    }

    private static void ValidateNullableText(string? value, int maximumLength)
    {
        if (value is not null)
        {
            ValidateText(value, maximumLength);
        }
    }

    private static bool? ReadNullableBoolean(
        JsonElement element,
        string propertyName) =>
        element.GetProperty(propertyName).ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw FormatFailure(),
        };

    private static bool ReadBoolean(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw FormatFailure(),
        };

    private static int ReadInt32(JsonElement element, string propertyName)
    {
        var value = ReadInt64(element, propertyName);
        return checked((int)value);
    }

    private static long ReadInt64(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value))
        {
            throw FormatFailure();
        }

        return value;
    }

    private static long? ReadNullableInt64(
        JsonElement element,
        string propertyName)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value))
        {
            throw FormatFailure();
        }

        return value;
    }

    private static byte ReadByte(JsonElement element, string propertyName) =>
        checked((byte)ReadInt64(element, propertyName));

    private static byte? ReadNullableByte(
        JsonElement element,
        string propertyName)
    {
        var value = ReadNullableInt64(element, propertyName);
        return value.HasValue ? checked((byte)value.Value) : null;
    }

    private static sbyte ReadSByte(JsonElement element, string propertyName) =>
        checked((sbyte)ReadInt64(element, propertyName));

    private static ushort ReadUInt16(JsonElement element, string propertyName) =>
        checked((ushort)ReadInt64(element, propertyName));

    private static uint ReadUInt32(JsonElement element, string propertyName) =>
        checked((uint)ReadInt64(element, propertyName));

    private static uint? ReadNullableUInt32(
        JsonElement element,
        string propertyName)
    {
        var value = ReadNullableInt64(element, propertyName);
        return value.HasValue ? checked((uint)value.Value) : null;
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static void WriteNullableBoolean(
        Utf8JsonWriter writer,
        string propertyName,
        bool? value)
    {
        if (value.HasValue)
        {
            writer.WriteBoolean(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableInt64(
        Utf8JsonWriter writer,
        string propertyName,
        long? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableByte(
        Utf8JsonWriter writer,
        string propertyName,
        byte? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static void WriteNullableUInt32(
        Utf8JsonWriter writer,
        string propertyName,
        uint? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(propertyName, value.Value);
        }
        else
        {
            writer.WriteNull(propertyName);
        }
    }

    private static InvalidDataException FormatFailure(
        Exception? innerException = null) =>
        new("The Bahrain lap audit document is malformed.", innerException);

    private static readonly (LapEvidenceFlags Flag, string Name)[] FlagNames =
    [
        (LapEvidenceFlags.InvalidationObserved, "invalidation-observed"),
        (LapEvidenceFlags.PitObserved, "pit-observed"),
        (LapEvidenceFlags.FlashbackObserved, "flashback-observed"),
        (LapEvidenceFlags.MaterialGapObserved, "material-gap-observed"),
        (LapEvidenceFlags.ContextChanged, "context-changed"),
        (LapEvidenceFlags.UnsupportedContext, "unsupported-context"),
        (LapEvidenceFlags.PlayerIndexChanged, "player-index-changed"),
        (LapEvidenceFlags.MissingContext, "missing-context"),
    ];
}
