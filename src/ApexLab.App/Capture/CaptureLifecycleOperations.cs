using ApexLab.App.Lifecycle;
using ApexLab.Application.Capture;
using Microsoft.Extensions.Hosting;

namespace ApexLab.App.Capture;

public sealed class CaptureLifecycleOperations(
    ICaptureWorkflow workflow) :
    IApplicationLifecycleOperations,
    IHostedService
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

    public Task StopProducersAsync(
        CancellationToken cancellationToken) =>
        workflow.StopProducersAsync(cancellationToken);

    public Task DrainWorkAsync(
        CancellationToken cancellationToken) =>
        workflow.DrainWorkAsync(cancellationToken);

    public Task FinalizeStoresAsync(
        CancellationToken cancellationToken) =>
        workflow.FinalizeStoresAsync(cancellationToken);

    Task IHostedService.StartAsync(
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    async Task IHostedService.StopAsync(
        CancellationToken cancellationToken)
    {
        await workflow.StopAsync(
                CaptureStopReason.HostShutdown,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
