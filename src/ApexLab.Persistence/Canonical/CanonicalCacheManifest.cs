using System.Text.Json;
using ApexLab.Application.Canonical;

namespace ApexLab.Persistence.Canonical;

internal sealed record CanonicalCacheManifest
{
    private static readonly string[] PropertyNames =
    [
        "manifestFormatVersion",
        "dataFormatVersion",
        "identitySha256",
        "sourceEvidenceSha256",
        "protocolId",
        "contractId",
        "decoderId",
        "canonicalSchemaId",
        "dataLeafName",
        "dataLengthBytes",
        "dataSha256",
        "canonicalSha256",
        "sourceStopwatchFrequency",
        "recordCount",
        "observationCount",
        "exclusionCount",
        "gapCount",
        "firstSourceSequence",
        "lastSourceSequence",
    ];

    private CanonicalCacheManifest(
        CanonicalCacheCompletion completion,
        string dataLeafName)
    {
        ValidateCompletion(completion);
        var expectedLeafName = CreateDataLeafName(completion);
        if (!StringComparer.Ordinal.Equals(dataLeafName, expectedLeafName))
        {
            throw new ArgumentException(
                "The canonical data leaf does not match its content identity.",
                nameof(dataLeafName));
        }

        Completion = completion;
        DataLeafName = dataLeafName;
    }

    public const ushort ManifestFormatVersion = 1;
    public const int MaximumLengthBytes = 8_192;

    public CanonicalCacheCompletion Completion { get; }

    public string DataLeafName { get; }

    public static CanonicalCacheManifest FromCompletion(
        CanonicalCacheCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        return new(completion, CreateDataLeafName(completion));
    }

    public byte[] Serialize()
    {
        using var stream = new MemoryStream(MaximumLengthBytes);
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = false }))
        {
            var identity = Completion.Identity;
            writer.WriteStartObject();
            writer.WriteNumber("manifestFormatVersion", ManifestFormatVersion);
            writer.WriteNumber(
                "dataFormatVersion",
                CanonicalCacheFormat.DataFormatVersion);
            writer.WriteString("identitySha256", identity.IdentitySha256);
            writer.WriteString(
                "sourceEvidenceSha256",
                identity.SourceEvidenceSha256);
            writer.WriteString("protocolId", identity.ProtocolId);
            writer.WriteString("contractId", identity.ContractId);
            writer.WriteString("decoderId", identity.DecoderId);
            writer.WriteString(
                "canonicalSchemaId",
                identity.CanonicalSchemaId);
            writer.WriteString("dataLeafName", DataLeafName);
            writer.WriteNumber("dataLengthBytes", Completion.DataLengthBytes);
            writer.WriteString("dataSha256", Completion.DataSha256);
            writer.WriteString("canonicalSha256", Completion.CanonicalSha256);
            writer.WriteNumber(
                "sourceStopwatchFrequency",
                Completion.SourceStopwatchFrequency);
            writer.WriteNumber("recordCount", Completion.RecordCount);
            writer.WriteNumber("observationCount", Completion.ObservationCount);
            writer.WriteNumber("exclusionCount", Completion.ExclusionCount);
            writer.WriteNumber("gapCount", Completion.GapCount);
            WriteNullableInt64(
                writer,
                "firstSourceSequence",
                Completion.FirstSourceSequence);
            WriteNullableInt64(
                writer,
                "lastSourceSequence",
                Completion.LastSourceSequence);
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        if (stream.Length > MaximumLengthBytes)
        {
            throw new InvalidOperationException(
                "The canonical manifest exceeded its bounded format.");
        }

        return stream.ToArray();
    }

    public static CanonicalCacheManifest Deserialize(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is < 2 or > MaximumLengthBytes)
        {
            throw FormatFailure();
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 4,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw FormatFailure();
            }

            RequireClosedProperties(root);
            RequireEqual(
                ReadUInt16(root, "manifestFormatVersion"),
                ManifestFormatVersion);
            RequireEqual(
                ReadUInt16(root, "dataFormatVersion"),
                CanonicalCacheFormat.DataFormatVersion);

            var identity = new CanonicalReplayIdentity(
                ReadString(root, "sourceEvidenceSha256"),
                ReadString(root, "protocolId"),
                ReadString(root, "contractId"),
                ReadString(root, "decoderId"),
                ReadString(root, "canonicalSchemaId"));
            if (!StringComparer.Ordinal.Equals(
                    identity.IdentitySha256,
                    ReadString(root, "identitySha256")))
            {
                throw FormatFailure();
            }

            var completion = new CanonicalCacheCompletion(
                identity,
                ReadInt64(root, "sourceStopwatchFrequency"),
                ReadInt64(root, "dataLengthBytes"),
                ReadString(root, "dataSha256"),
                ReadString(root, "canonicalSha256"),
                ReadInt64(root, "recordCount"),
                ReadInt64(root, "observationCount"),
                ReadInt64(root, "exclusionCount"),
                ReadInt64(root, "gapCount"),
                ReadNullableInt64(root, "firstSourceSequence"),
                ReadNullableInt64(root, "lastSourceSequence"));
            return new(completion, ReadString(root, "dataLeafName"));
        }
        catch (Exception exception) when (exception is
                   JsonException
                   or ArgumentException
                   or OverflowException)
        {
            throw FormatFailure(exception);
        }
    }

    public static string CreateManifestLeafName(CanonicalReplayIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return $"{identity.IdentitySha256}.apxcan.json";
    }

    private static string CreateDataLeafName(CanonicalCacheCompletion completion) =>
        $"{completion.Identity.IdentitySha256}.{completion.DataSha256}.apxcan";

    private static void ValidateCompletion(CanonicalCacheCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.DataLengthBytes
            is < CanonicalCacheFormat.HeaderLength + CanonicalCacheFormat.FooterLength
            or > CanonicalCacheFormat.MaximumDataLength
            || completion.RecordCount > CanonicalCacheFormat.MaximumRecordCount
            || completion.DataLengthBytes < checked(
                CanonicalCacheFormat.HeaderLength
                + CanonicalCacheFormat.FooterLength
                + (completion.RecordCount * 15)))
        {
            throw new ArgumentOutOfRangeException(nameof(completion));
        }
    }

    private static void RequireClosedProperties(JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!PropertyNames.Contains(property.Name, StringComparer.Ordinal)
                || !seen.Add(property.Name))
            {
                throw FormatFailure();
            }
        }

        if (seen.Count != PropertyNames.Length)
        {
            throw FormatFailure();
        }
    }

    private static string ReadString(JsonElement root, string propertyName)
    {
        var property = root.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw FormatFailure();
        }

        return property.GetString() ?? throw FormatFailure();
    }

    private static ushort ReadUInt16(JsonElement root, string propertyName)
    {
        var value = ReadInt64(root, propertyName);
        return checked((ushort)value);
    }

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        var property = root.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt64(out var value))
        {
            throw FormatFailure();
        }

        return value;
    }

    private static long? ReadNullableInt64(
        JsonElement root,
        string propertyName)
    {
        var property = root.GetProperty(propertyName);
        return property.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number when property.TryGetInt64(out var value) => value,
            _ => throw FormatFailure(),
        };
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

    private static void RequireEqual<T>(T actual, T expected)
        where T : IEquatable<T>
    {
        if (!actual.Equals(expected))
        {
            throw FormatFailure();
        }
    }

    private static InvalidDataException FormatFailure(
        Exception? innerException = null) =>
        new("The canonical cache manifest is malformed.", innerException);
}
