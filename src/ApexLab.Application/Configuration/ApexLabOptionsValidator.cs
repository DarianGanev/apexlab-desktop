using System.Net;
using ApexLab.Application.Storage;

namespace ApexLab.Application.Configuration;

public sealed record ApexLabOptionsValidationFailure(string FieldName, string Message);

public static class ApexLabOptionsValidator
{
    public static IReadOnlyList<ApexLabOptionsValidationFailure> Validate(ApexLabOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<ApexLabOptionsValidationFailure>();

        if (!IPAddress.TryParse(options.BindAddress, out var bindAddress))
        {
            failures.Add(new(
                nameof(ApexLabOptions.BindAddress),
                "Bind address must be a valid IP address."));
        }
        else if (!options.AllowLan && !IPAddress.IsLoopback(bindAddress))
        {
            failures.Add(new(
                nameof(ApexLabOptions.BindAddress),
                "Bind address must be loopback while LAN capture is disabled."));
        }

        if (options.UdpPort is < 1 or > 65_535)
        {
            failures.Add(new(
                nameof(ApexLabOptions.UdpPort),
                "UDP port must be between 1 and 65535."));
        }

        if (options.LiveSnapshotRateHz is < 1 or > 20)
        {
            failures.Add(new(
                nameof(ApexLabOptions.LiveSnapshotRateHz),
                "Live snapshot rate must be between 1 and 20 Hz."));
        }

        if (options.RawChunkDuration <= TimeSpan.Zero)
        {
            failures.Add(new(
                nameof(ApexLabOptions.RawChunkDuration),
                "Raw chunk duration must be positive."));
        }

        try
        {
            _ = ApplicationPaths.FromRoot(options.DataRootPath);
        }
        catch (ArgumentException)
        {
            failures.Add(new(
                nameof(ApexLabOptions.DataRootPath),
                "Data root must be a safe absolute non-root path."));
        }

        if (options.StorageQuotaBytes <= 0)
        {
            failures.Add(new(
                nameof(ApexLabOptions.StorageQuotaBytes),
                "Storage quota must be positive."));
        }

        return failures.AsReadOnly();
    }
}
