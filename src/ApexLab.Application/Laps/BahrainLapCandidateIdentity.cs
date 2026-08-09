using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ApexLab.Domain.Laps;

namespace ApexLab.Application.Laps;

public static class BahrainLapCandidateIdentity
{
    private static ReadOnlySpan<byte> HashDomain =>
        "ApexLab.BahrainLapCandidate.v1\0"u8;

    public static LapCandidateId Calculate(
        string canonicalIdentitySha256,
        LapBoundary boundary,
        LapEvidenceFlags evidenceFlags,
        BahrainTelemetryContext? context)
    {
        LapIdentityValidation.RequireSha256(
            canonicalIdentitySha256,
            nameof(canonicalIdentitySha256));
        ArgumentNullException.ThrowIfNull(boundary);
        LapEvidence.Validate(evidenceFlags);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(HashDomain);
        AppendString(hash, canonicalIdentitySha256);
        AppendString(hash, BahrainLapAuditContract.AuditId);
        AppendByte(hash, checked((byte)boundary.Completeness));
        AppendInt64(hash, boundary.StartSourceSequence);
        AppendInt64(hash, boundary.EndSourceSequenceExclusive);
        AppendNullableInt64(hash, boundary.CompletionEvidenceSourceSequence);
        AppendNullableByte(hash, boundary.LapNumber);
        AppendNullableUInt32(hash, boundary.OfficialLapTimeMilliseconds);
        AppendUInt32(hash, checked((uint)evidenceFlags));
        AppendByte(hash, context is null ? (byte)0 : (byte)1);
        if (context is not null)
        {
            AppendString(hash, context.IdentitySha256);
        }

        return new(Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendUInt32(hash, checked((uint)bytes.Length));
        hash.AppendData(bytes);
    }

    private static void AppendNullableInt64(IncrementalHash hash, long? value)
    {
        AppendByte(hash, value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            AppendInt64(hash, value.Value);
        }
    }

    private static void AppendNullableByte(IncrementalHash hash, byte? value)
    {
        AppendByte(hash, value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            AppendByte(hash, value.Value);
        }
    }

    private static void AppendNullableUInt32(IncrementalHash hash, uint? value)
    {
        AppendByte(hash, value.HasValue ? (byte)1 : (byte)0);
        if (value.HasValue)
        {
            AppendUInt32(hash, value.Value);
        }
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendUInt32(IncrementalHash hash, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        hash.AppendData(buffer);
    }

    private static void AppendByte(IncrementalHash hash, byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        hash.AppendData(buffer);
    }
}

internal static class LapIdentityValidation
{
    public static void RequireSha256(string value, string parameterName)
    {
        if (value is null
            || value.Length != 64
            || value.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "A lowercase SHA-256 value is required.",
                parameterName);
        }
    }
}
