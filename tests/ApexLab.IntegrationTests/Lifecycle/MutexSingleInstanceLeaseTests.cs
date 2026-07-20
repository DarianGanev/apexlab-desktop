using ApexLab.App.Lifecycle;

namespace ApexLab.IntegrationTests.Lifecycle;

[TestClass]
[DoNotParallelize]
public sealed class MutexSingleInstanceLeaseTests
{
    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public void First_acquirer_wins_second_is_denied_and_release_allows_reacquisition()
    {
        var identity = $"ApexLab.Tests.{Guid.NewGuid():N}";
        using var first = new MutexSingleInstanceLease(identity);
        using var second = new MutexSingleInstanceLease(identity);

        Assert.IsTrue(first.TryAcquire());
        Assert.IsFalse(second.TryAcquire());
        first.Release();

        using var third = new MutexSingleInstanceLease(identity);
        Assert.IsTrue(third.TryAcquire());
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public void Failed_acquisition_and_repeated_release_are_safe()
    {
        var identity = $"ApexLab.Tests.{Guid.NewGuid():N}";
        using var first = new MutexSingleInstanceLease(identity);
        using var denied = new MutexSingleInstanceLease(identity);
        Assert.IsTrue(first.TryAcquire());
        Assert.IsFalse(denied.TryAcquire());

        denied.Release();
        denied.Release();
        first.Release();
        first.Release();
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public void Abandoned_mutex_is_treated_as_successfully_acquired()
    {
        var identity = $"ApexLab.Tests.{Guid.NewGuid():N}";
        using var subject = new MutexSingleInstanceLease(identity);
        using var keeper = new Mutex(initiallyOwned: false, subject.ScopedName);
        using var acquired = new ManualResetEventSlim(false);
        var abandoningThread = new Thread(() =>
        {
            using var abandoned = new Mutex(initiallyOwned: false, subject.ScopedName);
            abandoned.WaitOne();
            acquired.Set();
        });
        abandoningThread.Start();
        Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(abandoningThread.Join(TimeSpan.FromSeconds(1)));

        Assert.IsTrue(subject.TryAcquire());
    }

    [TestMethod]
    [Timeout(3_000, CooperativeCancellation = true)]
    public void Lease_name_is_stable_for_same_app_and_distinct_for_another_app()
    {
        var one = MutexSingleInstanceLease.CreateScopedName("ApexLab.Tests.One", "S-1-5-21-test");
        var same = MutexSingleInstanceLease.CreateScopedName("ApexLab.Tests.One", "S-1-5-21-test");
        var otherApp = MutexSingleInstanceLease.CreateScopedName("ApexLab.Tests.Two", "S-1-5-21-test");
        var otherUser = MutexSingleInstanceLease.CreateScopedName("ApexLab.Tests.One", "S-1-5-21-other");

        Assert.AreEqual(one, same);
        Assert.AreNotEqual(one, otherApp);
        Assert.AreNotEqual(one, otherUser);
        StringAssert.StartsWith(one, @"Local\ApexLab.");
        Assert.IsFalse(one.Contains("S-1-5-21-test", StringComparison.Ordinal));
    }

    [TestMethod]
    [Timeout(5_000, CooperativeCancellation = true)]
    public async Task Concurrent_acquirers_share_result_and_release_waits_for_inflight_callers()
    {
        using var ownerReached = new ManualResetEventSlim(false);
        using var publishResult = new ManualResetEventSlim(false);
        using var bothCallersEntered = new CountdownEvent(2);
        var identity = $"ApexLab.Tests.{Guid.NewGuid():N}";
        using var subject = new MutexSingleInstanceLease(
            identity,
            $"test-user-{Guid.NewGuid():N}",
            new MutexSingleInstanceLeaseTestHooks
            {
                AcquirerEntered = () => bothCallersEntered.Signal(),
                BeforePublishingAcquisition = () =>
                {
                    ownerReached.Set();
                    publishResult.Wait();
                },
            });
        using var callersReady = new Barrier(3);

        var first = Task.Run(() =>
        {
            Assert.IsTrue(callersReady.SignalAndWait(TimeSpan.FromSeconds(1)));
            return subject.TryAcquire();
        });
        var second = Task.Run(() =>
        {
            Assert.IsTrue(callersReady.SignalAndWait(TimeSpan.FromSeconds(1)));
            return subject.TryAcquire();
        });
        Assert.IsTrue(callersReady.SignalAndWait(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(bothCallersEntered.Wait(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(ownerReached.Wait(TimeSpan.FromSeconds(1)));
        var release = Task.Run(subject.Release);

        publishResult.Set();

        Assert.IsTrue(await first.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.IsTrue(await second.WaitAsync(TimeSpan.FromSeconds(1)));
        await release.WaitAsync(TimeSpan.FromSeconds(1));
    }
}
