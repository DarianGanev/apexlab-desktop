namespace ApexLab.Application.Capture;

public sealed record RawEvidenceCaptureId
{
    private const int EncodedLength = 32;

    private RawEvidenceCaptureId(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static RawEvidenceCaptureId Create()
    {
        return new RawEvidenceCaptureId(Guid.NewGuid().ToString("N"));
    }

    public static RawEvidenceCaptureId Parse(string value)
    {
        if (!TryParse(value, out var parsed))
        {
            throw new FormatException(
                "A canonical lowercase RFC version-4 capture identity is required.");
        }

        return parsed!;
    }

    public static bool TryParse(
        string? value,
        out RawEvidenceCaptureId? captureId)
    {
        if (value is null
            || value.Length != EncodedLength
            || value[12] != '4'
            || value[16] is not ('8' or '9' or 'a' or 'b')
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            captureId = null;
            return false;
        }

        captureId = new RawEvidenceCaptureId(value);
        return true;
    }

    public override string ToString() => Value;
}
