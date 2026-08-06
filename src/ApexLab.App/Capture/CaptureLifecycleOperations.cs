using ApexLab.App.Lifecycle;
using ApexLab.Application.Capture;

namespace ApexLab.App.Capture;

public sealed class CaptureLifecycleOperations(
    ICaptureWorkflow workflow) : IApplicationLifecycleOperations
{
    public Task ValidateSettingsAsync(
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task PrepareDataRootAsync(
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RollbackDataRootAsync(
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StartProducersAsync(
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StopProducersAsync(CancellationToken cancellationToken)
    {
        workflow.BeginStop(CaptureStopReason.HostShutdown);
        return workflow.StopProducersAsync(CancellationToken.None);
    }

    public Task DrainWorkAsync(
        CancellationToken cancellationToken) =>
        workflow.DrainWorkAsync(CancellationToken.None);

    public Task FinalizeStoresAsync(
        CancellationToken cancellationToken) =>
        workflow.FinalizeStoresAsync(CancellationToken.None);
}
