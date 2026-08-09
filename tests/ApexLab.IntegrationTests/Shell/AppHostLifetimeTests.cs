using ApexLab.App;
using Microsoft.Extensions.Hosting;

namespace ApexLab.IntegrationTests.Shell;

[TestClass]
public sealed class AppHostLifetimeTests
{
    [TestMethod]
    public void Normal_start_and_repeated_exit_start_and_show_once_then_release_once()
    {
        var host = new RecordingHost();
        var showCount = 0;
        var subject = new AppHostLifetime(host, _ => showCount++);

        subject.Start();
        subject.Start();
        subject.Dispose();
        subject.Dispose();

        Assert.AreEqual(1, host.StartCount);
        Assert.AreEqual(1, showCount);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Start_failure_disposes_ownership_without_showing_or_stopping()
    {
        var failure = new InvalidOperationException("start failed");
        var host = new RecordingHost { StartFailure = failure };
        var showCount = 0;
        var subject = new AppHostLifetime(host, _ => showCount++);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Start);

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(0, showCount);
        Assert.AreEqual(0, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Resolve_or_show_failure_stops_started_host_and_disposes_ownership()
    {
        var failure = new InvalidOperationException("show failed");
        var host = new RecordingHost();
        var subject = new AppHostLifetime(host, _ => throw failure);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Start);

        Assert.AreSame(failure, thrown);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Startup_failure_is_first_when_cleanup_also_fails()
    {
        var primary = new InvalidOperationException("show failed");
        var stopFailure = new InvalidOperationException("stop failed");
        var disposeFailure = new InvalidOperationException("dispose failed");
        var host = new RecordingHost
        {
            StopFailure = stopFailure,
            DisposeFailure = disposeFailure,
        };
        var subject = new AppHostLifetime(host, _ => throw primary);

        var thrown = Assert.ThrowsExactly<AggregateException>(subject.Start);

        CollectionAssert.AreEqual(
            new Exception[] { primary, stopFailure, disposeFailure },
            thrown.InnerExceptions.ToArray());
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Start_failure_remains_primary_when_dispose_also_fails()
    {
        var primary = new InvalidOperationException("start failed");
        var disposeFailure = new InvalidOperationException("dispose failed");
        var host = new RecordingHost
        {
            StartFailure = primary,
            DisposeFailure = disposeFailure,
        };
        var subject = new AppHostLifetime(host, _ => Assert.Fail("Show must not run."));

        var thrown = Assert.ThrowsExactly<AggregateException>(subject.Start);

        CollectionAssert.AreEqual(
            new Exception[] { primary, disposeFailure },
            thrown.InnerExceptions.ToArray());
        Assert.AreEqual(0, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Stop_failure_still_disposes_and_repeated_exit_does_not_retry_cleanup()
    {
        var stopFailure = new InvalidOperationException("stop failed");
        var host = new RecordingHost { StopFailure = stopFailure };
        var subject = new AppHostLifetime(host, _ => { });
        subject.Start();

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Dispose);
        subject.Dispose();

        Assert.AreSame(stopFailure, thrown);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    [TestMethod]
    public void Dispose_failure_is_reported_once_after_ownership_is_released()
    {
        var disposeFailure = new InvalidOperationException("dispose failed");
        var host = new RecordingHost { DisposeFailure = disposeFailure };
        var subject = new AppHostLifetime(host, _ => { });
        subject.Start();

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(subject.Dispose);
        subject.Dispose();

        Assert.AreSame(disposeFailure, thrown);
        Assert.AreEqual(1, host.StopCount);
        Assert.AreEqual(1, host.DisposeCount);
    }

    private sealed class RecordingHost : IHost
    {
        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public Exception? StartFailure { get; init; }

        public Exception? StopFailure { get; init; }

        public Exception? DisposeFailure { get; init; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public int DisposeCount { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            return StartFailure is null
                ? Task.CompletedTask
                : Task.FromException(StartFailure);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            return StopFailure is null
                ? Task.CompletedTask
                : Task.FromException(StopFailure);
        }

        public void Dispose()
        {
            DisposeCount++;
            if (DisposeFailure is not null)
            {
                throw DisposeFailure;
            }
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
