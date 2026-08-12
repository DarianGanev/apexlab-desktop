namespace ApexLab.Domain.Laps;

public sealed record LapCandidateId
{
    public LapCandidateId(string value)
    {
        if (value is null
            || value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 lap candidate identity is required.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
