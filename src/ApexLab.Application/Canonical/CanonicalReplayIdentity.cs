using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ApexLab.Application.Canonical;

public sealed record CanonicalReplayIdentity
{
    private static ReadOnlySpan<byte> HashDomain =>
        "ApexLab.CanonicalReplayIdentity.v1\0"u8;

    public CanonicalReplayIdentity(
        string sourceEvidenceSha256,
        string protocolId,
        string contractId,
        string decoderId,
        string canonicalSchemaId)
    {
        CanonicalIdentityValidation.RequireSha256(
            sourceEvidenceSha256,
            nameof(sourceEvidenceSha256));
        CanonicalIdentityValidation.RequireVersionId(protocolId, nameof(protocolId));
        CanonicalIdentityValidation.RequireVersionId(contractId, nameof(contractId));
        CanonicalIdentityValidation.RequireVersionId(decoderId, nameof(decoderId));
        CanonicalIdentityValidation.RequireVersionId(
            canonicalSchemaId,
            nameof(canonicalSchemaId));

        SourceEvidenceSha256 = sourceEvidenceSha256;
        ProtocolId = protocolId;
        ContractId = contractId;
        DecoderId = decoderId;
        CanonicalSchemaId = canonicalSchemaId;
        IdentitySha256 = CalculateDigest(
            sourceEvidenceSha256,
            protocolId,
            contractId,
            decoderId,
            canonicalSchemaId);
    }

    public string SourceEvidenceSha256 { get; }
    public string ProtocolId { get; }
    public string ContractId { get; }
    public string DecoderId { get; }
    public string CanonicalSchemaId { get; }
    public string IdentitySha256 { get; }

    private static string CalculateDigest(params string[] components)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(HashDomain);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        foreach (var component in components)
        {
            var bytes = Encoding.UTF8.GetBytes(component);
            BinaryPrimitives.WriteUInt32LittleEndian(
                length,
                checked((uint)bytes.Length));
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal static class CanonicalIdentityValidation
{
    public static void RequireSha256(string value, string parameterName)
    {
        if (value is null
            || value.Length != 64
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 value is required.",
                parameterName);
        }
    }

    public static void RequireVersionId(string value, string parameterName)
    {
        if (value is not { Length: >= 1 and <= 64 }
            || !IsLowerLetterOrDigit(value[0])
            || value.Skip(1).Any(character =>
                !IsLowerLetterOrDigit(character)
                && character is not ('.' or '-')))
        {
            throw new ArgumentException(
                "A bounded canonical version identity is required.",
                parameterName);
        }
    }

    private static bool IsLowerLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z'
            or >= '0' and <= '9';
}
