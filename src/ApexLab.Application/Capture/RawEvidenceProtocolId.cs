namespace ApexLab.Application.Capture;

public sealed record RawEvidenceProtocolId
{
    private RawEvidenceProtocolId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static RawEvidenceProtocolId Parse(string value)
    {
        if (!TryParse(value, out var parsed))
        {
            throw new FormatException(
                "A canonical raw-evidence protocol identity is required.");
        }

        return parsed!;
    }

    public static bool TryParse(
        string? value,
        out RawEvidenceProtocolId? protocolId)
    {
        if (value is not { Length: >= 1 and <= 64 }
            || !IsLowerLetterOrDigit(value[0])
            || value.Skip(1).Any(character =>
                !IsLowerLetterOrDigit(character)
                && character is not ('.' or '-')))
        {
            protocolId = null;
            return false;
        }

        protocolId = new RawEvidenceProtocolId(value);
        return true;
    }

    public override string ToString() => Value;

    private static bool IsLowerLetterOrDigit(char value)
    {
        return value is >= 'a' and <= 'z'
            or >= '0' and <= '9';
    }
}
