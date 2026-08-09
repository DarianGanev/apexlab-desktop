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

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(length, checked((uint)bytes.Length));
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}
