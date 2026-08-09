using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ApexLab.Application.Canonical;

namespace ApexLab.Persistence.Canonical;

internal static class CanonicalCacheHash
{
    private static ReadOnlySpan<byte> Domain =>
        "ApexLab.CanonicalCache.v1\0"u8;

    public static IncrementalHash CreateCanonicalHash(
        CanonicalReplayIdentity identity,
        long dataLength)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataLength, 0);
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Domain);
        AppendString(hash, identity.IdentitySha256);
        AppendString(hash, identity.SourceEvidenceSha256);
        AppendString(hash, identity.ProtocolId);
        AppendString(hash, identity.ContractId);
        AppendString(hash, identity.DecoderId);
        AppendString(hash, identity.CanonicalSchemaId);
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(length, checked((ulong)dataLength));
        hash.AppendData(length);
        return hash;
    }

    public static async Task<CanonicalCacheHashes> CalculateAsync(
        Stream stream,
        CanonicalReplayIdentity identity,
        long dataLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException(
                "A readable seekable canonical stream is required.",
                nameof(stream));
        }

        stream.Position = 0;
        using var dataHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var canonicalHash = CreateCanonicalHash(identity, dataLength);
        var buffer = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)
                   .ConfigureAwait(false)) != 0)
        {
            dataHash.AppendData(buffer, 0, read);
            canonicalHash.AppendData(buffer, 0, read);
        }

        return new(
            Convert.ToHexStringLower(dataHash.GetHashAndReset()),
            Convert.ToHexStringLower(canonicalHash.GetHashAndReset()));
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

internal sealed record CanonicalCacheHashes(
    string DataSha256,
    string CanonicalSha256);
