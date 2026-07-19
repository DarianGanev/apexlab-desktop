using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;

namespace ApexLab.App;

public sealed class AppHostLifetime : IDisposable
{
    private readonly IHost _host;
    private readonly Action<IServiceProvider> _showMainWindow;
    private readonly TimeSpan _shutdownTimeout;
    private bool _hostStarted;
    private bool _ownershipReleased;

    public AppHostLifetime(
        IHost host,
        Action<IServiceProvider> showMainWindow,
        TimeSpan? shutdownTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(showMainWindow);

        _host = host;
        _showMainWindow = showMainWindow;
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(5);
        if (_shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(shutdownTimeout),
                "Shutdown timeout must be positive.");
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_ownershipReleased, this);
        if (_hostStarted)
        {
            return;
        }

        try
        {
            _host.StartAsync().GetAwaiter().GetResult();
            _hostStarted = true;
            _showMainWindow(_host.Services);
        }
        catch (Exception primaryFailure)
        {
            var cleanupFailures = ReleaseOwnership();
            ThrowPreservingPrimary(primaryFailure, cleanupFailures);
        }
    }

    public void Dispose()
    {
        var cleanupFailures = ReleaseOwnership();
        ThrowCleanupFailures(cleanupFailures);
    }

    private List<Exception> ReleaseOwnership()
    {
        if (_ownershipReleased)
        {
            return [];
        }

        _ownershipReleased = true;
        var failures = new List<Exception>();
        if (_hostStarted)
        {
            try
            {
                _host.StopAsync(_shutdownTimeout).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        try
        {
            _host.Dispose();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        return failures;
    }

    private static void ThrowPreservingPrimary(
        Exception primaryFailure,
        IReadOnlyList<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            throw new UnreachableException();
        }

        throw new AggregateException(
            "ApexLab startup failed and host cleanup also reported failures.",
            new[] { primaryFailure }.Concat(cleanupFailures));
    }

    private static void ThrowCleanupFailures(IReadOnlyList<Exception> cleanupFailures)
    {
        if (cleanupFailures.Count == 0)
        {
            return;
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
            throw new UnreachableException();
        }

        throw new AggregateException(
            "ApexLab host shutdown reported multiple failures.",
            cleanupFailures);
    }
}
