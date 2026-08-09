using System.Reflection;
using ApexLab.Application.Capture;
using ApexLab.Telemetry.Abstractions.Capture;

namespace ApexLab.Application.Tests.Capture;

[TestClass]
public sealed class RawEvidenceStoreContractTests
{
    [TestMethod]
    public void StoreContractOwnsWriteFinalizeAndAsyncDisposal()
    {
        Assert.IsTrue(typeof(IAsyncDisposable).IsAssignableFrom(typeof(IRawEvidenceStore)));
        Assert.IsNotNull(typeof(IRawEvidenceStore).GetMethod(
            nameof(IRawEvidenceStore.WriteAsync),
            [typeof(DatagramEnvelope), typeof(CancellationToken)]));
        Assert.IsNotNull(typeof(IRawEvidenceStore).GetMethod(
            nameof(IRawEvidenceStore.FinalizeAsync),
            [typeof(CancellationToken)]));
    }

    [TestMethod]
    public void PublicContractDoesNotExposeFilesystemPaths()
    {
        var publicNames = typeof(IRawEvidenceStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Select(member => member.Name)
            .Concat(typeof(RawEvidenceCompletion)
                .GetProperties()
                .Select(property => property.Name))
            .ToArray();

        CollectionAssert.DoesNotContain(publicNames, "Path");
        CollectionAssert.DoesNotContain(publicNames, "FilePath");
        CollectionAssert.DoesNotContain(publicNames, "Directory");
        CollectionAssert.DoesNotContain(publicNames, "ManifestPath");
        CollectionAssert.DoesNotContain(publicNames, "DataPath");
    }

    [TestMethod]
    public void CompletionRequiresCanonicalDigestAndMatchingIdentity()
    {
        var captureId = RawEvidenceCaptureId.Parse(
            "00112233445546778899aabbccddeeff");
        var protocolId = RawEvidenceProtocolId.Parse("ea-f1-25-v3");
        var completion = new RawEvidenceCompletion(
            captureId,
            protocolId,
            recordCount: 2,
            dataLengthBytes: 256,
            new string('a', 64),
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(captureId, completion.CaptureId);
        Assert.AreEqual("ea-f1-25-v3", completion.ProtocolId.Value);
        Assert.AreEqual(2L, completion.RecordCount);
        Assert.ThrowsExactly<ArgumentException>(
            () => new RawEvidenceCompletion(
                captureId,
                protocolId,
                0,
                136,
                new string('A', 64),
                DateTimeOffset.UnixEpoch));
    }
}
