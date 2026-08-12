using System.Text.Json;
using ApexLab.Application.Laps;

namespace ApexLab.Replay.LapAudit;

internal static class BahrainLapAuditPrivateInput
{
    private const int MaximumCharacters = 2_048;

    private static readonly HashSet<string> ExpectedProperties =
        new(
        [
            "gameBuild",
            "playerVehicle",
            "controllerProfile",
            "setupDescriptor",
            "tyreCompound",
            "evidenceIntegrityPassed",
            "trackAndModeVisuallyConfirmed",
            "setupUnchanged",
            "contextCrossCheckPassed",
        ], StringComparer.Ordinal);

    public static async Task<BahrainLapAuditManualInputs?> TryReadAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();
        var buffer = new char[MaximumCharacters + 1];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await reader.ReadAsync(
                        buffer.AsMemory(length),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length > MaximumCharacters)
            {
                return null;
            }

            var input = buffer.AsSpan(0, length);
            if (!input.IsEmpty && input[0] == '\uFEFF')
            {
                input = input[1..];
            }

            return TryParse(input);
        }
        finally
        {
            Array.Clear(buffer);
        }
    }

    private static BahrainLapAuditManualInputs? TryParse(
        ReadOnlySpan<char> json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json.ToString(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 3,
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!ExpectedProperties.Contains(property.Name)
                    || !seen.Add(property.Name))
                {
                    return null;
                }
            }

            if (seen.Count != ExpectedProperties.Count)
            {
                return null;
            }

            var inputs = new BahrainLapAuditManualInputs(
                RequiredString(root, "gameBuild"),
                RequiredString(root, "playerVehicle"),
                RequiredString(root, "controllerProfile"),
                RequiredString(root, "setupDescriptor"),
                RequiredString(root, "tyreCompound"),
                RequiredBoolean(root, "evidenceIntegrityPassed"),
                RequiredBoolean(root, "trackAndModeVisuallyConfirmed"),
                RequiredBoolean(root, "setupUnchanged"),
                RequiredBoolean(root, "contextCrossCheckPassed"));
            return inputs.ToDomain().IsFullyConfirmed ? inputs : null;
        }
        catch (Exception exception) when (exception is
                   JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        return property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new JsonException();
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : throw new JsonException();
    }
}
