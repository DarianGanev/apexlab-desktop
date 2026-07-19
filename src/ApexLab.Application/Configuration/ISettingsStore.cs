namespace ApexLab.Application.Configuration;

public interface ISettingsStore : IDisposable
{
    Task<ApexLabOptions> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ApexLabOptions options, CancellationToken cancellationToken);
}
