using System.Text.Json;
using ApexLab.Application.Capture;

namespace ApexLab.Persistence.Raw;

internal static class RawEvidenceManifestParser
{
    private static readonly string[] RootPropertyNames =
    [
        "schemaVersion",
        "dataFormatVersion",
        "captureId",
        "protocolId",
        "dataFileName",
        "dataLengthBytes",
        "sha256",
        "recordCount",
        "firstSequence",
        "lastSequence",
        "firstArrivalTimestamp",
        "lastArrivalTimestamp",
        "stopwatchFrequency",
        "createdUtcTicks",
        "finalizedUtcTicks",
        "limits",
    ];

    private static readonly string[] LimitPropertyNames =
    [
        "maximumDurationMilliseconds",
        "maximumFileBytes",
        "minimumFreeSpaceBytes",
        "maximumPayloadBytes",
    ];

    public static RawEvidenceManifestDocument Parse(
        ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is < 1 or > RawEvidenceFormat.MaximumManifestBytes)
        {
            throw new InvalidDataException(
                "The raw evidence manifest length is outside the v1 bound.");
        }

        try
        {
            using var document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3,
                });
            var root = document.RootElement;
            RequireProperties(root, RootPropertyNames, "manifest");
            RequireInt64(root, "schemaVersion", expected: 1);
            RequireInt64(root, "dataFormatVersion", expected: 1);

            var captureId = RawEvidenceCaptureId.Parse(
                RequireString(root, "captureId"));
            var protocolId = RawEvidenceProtocolId.Parse(
                RequireString(root, "protocolId"));
            if (RequireString(root, "dataFileName")
                != $"{captureId.Value}.apxraw")
            {
                throw new InvalidDataException(
                    "The manifest data filename is not derived from its capture ID.");
            }

            var digestText = RequireString(root, "sha256");
            if (digestText.Length != 64
                || digestText.Any(character =>
                    character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
            {
                throw new InvalidDataException(
                    "The manifest requires a lowercase SHA-256 value.");
            }

            var limitsElement = RequireObject(root, "limits");
            RequireProperties(
                limitsElement,
                LimitPropertyNames,
                "manifest limits");
            var maximumDurationMilliseconds = RequireInt64(
                limitsElement,
                "maximumDurationMilliseconds");
            var maximumPayloadBytes = checked((int)RequireInt64(
                limitsElement,
                "maximumPayloadBytes"));
            var limits = new RawEvidenceLimits(
                TimeSpan.FromMilliseconds(
                    maximumDurationMilliseconds),
                RequireInt64(limitsElement, "maximumFileBytes"),
                RequireInt64(limitsElement, "minimumFreeSpaceBytes"),
                maximumPayloadBytes);
            var manifest = new RawEvidenceManifest(
                captureId,
                protocolId,
                RequireInt64(root, "dataLengthBytes"),
                RequireInt64(root, "recordCount"),
                RequireNullableInt64(root, "firstSequence"),
                RequireNullableInt64(root, "lastSequence"),
                RequireNullableInt64(
                    root,
                    "firstArrivalTimestamp"),
                RequireNullableInt64(
                    root,
                    "lastArrivalTimestamp"),
                RequireInt64(root, "stopwatchFrequency"),
                RequireInt64(root, "createdUtcTicks"),
                RequireInt64(root, "finalizedUtcTicks"),
                limits);
            var digest = Convert.FromHexString(digestText);
            var preimage =
                RawEvidenceFormat.SerializeManifestPreimage(manifest);
            var canonical = RawEvidenceFormat.ApplyDigest(
                preimage,
                digest);
            if (!bytes.Span.SequenceEqual(canonical))
            {
                throw new InvalidDataException(
                    "The raw evidence manifest is not canonical v1 JSON.");
            }

            return new RawEvidenceManifestDocument(
                manifest,
                preimage,
                digest);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or FormatException
                or ArgumentException
                or OverflowException
                or InvalidOperationException)
        {
            throw new InvalidDataException(
                "The raw evidence manifest is invalid.",
                exception);
        }
    }

    private static void RequireProperties(
        JsonElement element,
        string[] expected,
        string description)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The raw evidence {description} must be an object.");
        }

        var index = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (index >= expected.Length
                || property.Name != expected[index])
            {
                throw new InvalidDataException(
                    $"The raw evidence {description} property order is invalid.");
            }

            index++;
        }

        if (index != expected.Length)
        {
            throw new InvalidDataException(
                $"The raw evidence {description} properties are incomplete.");
        }
    }

    private static JsonElement RequireObject(
        JsonElement parent,
        string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"The manifest property '{name}' must be an object.");
        }

        return value;
    }

    private static string RequireString(
        JsonElement parent,
        string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"The manifest property '{name}' must be a string.");
        }

        return value.GetString()
            ?? throw new InvalidDataException(
                $"The manifest property '{name}' cannot be null.");
    }

    private static long RequireInt64(
        JsonElement parent,
        string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"The manifest property '{name}' must be an Int64.");
        }

        return result;
    }

    private static void RequireInt64(
        JsonElement parent,
        string name,
        long expected)
    {
        if (RequireInt64(parent, name) != expected)
        {
            throw new InvalidDataException(
                $"The manifest property '{name}' has an unsupported value.");
        }
    }

    private static long? RequireNullableInt64(
        JsonElement parent,
        string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"The manifest property '{name}' must be null or Int64.");
        }

        return result;
    }
}

internal sealed record RawEvidenceManifestDocument(
    RawEvidenceManifest Manifest,
    byte[] Preimage,
    byte[] Digest);
