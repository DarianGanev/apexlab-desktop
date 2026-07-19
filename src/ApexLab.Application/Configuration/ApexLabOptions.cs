using System.Net;

namespace ApexLab.Application.Configuration;

public sealed record ApexLabOptions(string DataRootPath)
{
    public string BindAddress { get; init; } = IPAddress.Loopback.ToString();

    public int UdpPort { get; init; } = 20_777;

    public bool AllowLan { get; init; }

    public int LiveSnapshotRateHz { get; init; } = 8;

    public TimeSpan RawChunkDuration { get; init; } = TimeSpan.FromSeconds(5);

    public long StorageQuotaBytes { get; init; } = 20L * 1024 * 1024 * 1024;
}
