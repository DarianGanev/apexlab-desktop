using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ApexLab.App.Lifecycle;

internal sealed class MutexSingleInstanceLeaseTestHooks
{
    public Action? AcquirerEntered { get; init; }
    public Action? BeforePublishingAcquisition { get; init; }
    public Action? ReleaseWaitingForAcquirers { get; init; }
}

public sealed class MutexSingleInstanceLease : ISingleInstanceLease
{
    private readonly object _gate = new();
    private readonly string _mutexName;
    private readonly ManualResetEventSlim _acquisitionCompleted = new(false);
    private readonly ManualResetEventSlim _releaseRequested = new(false);
    private readonly MutexSingleInstanceLeaseTestHooks? _testHooks;
    private Thread? _ownerThread;
    private Exception? _ownerFailure;
    private int _acquired;
    private bool _attempted;
    private bool _releaseInitiated;
    private bool _released;
    private int _inflightAcquirers;

    public MutexSingleInstanceLease(string applicationIdentity)
        : this(applicationIdentity, GetCurrentUserIdentity(), null)
    {
    }

    internal MutexSingleInstanceLease(
        string applicationIdentity,
        string userIdentity,
        MutexSingleInstanceLeaseTestHooks? testHooks)
    {
        _mutexName = CreateScopedName(applicationIdentity, userIdentity);
        _testHooks = testHooks;
    }

    public string ScopedName => _mutexName;

    public bool TryAcquire()
    {
        var startOwner = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_releaseInitiated || _released, this);
            _inflightAcquirers++;
            _testHooks?.AcquirerEntered?.Invoke();
            if (!_attempted)
            {
                _attempted = true;
                startOwner = true;
                _ownerThread = new Thread(OwnMutex)
                {
                    IsBackground = true,
                    Name = "ApexLab single-instance mutex owner",
                };
            }
        }

        try
        {
            if (startOwner)
            {
                _ownerThread!.Start();
            }

            _acquisitionCompleted.Wait();
            if (_ownerFailure is not null)
            {
                ExceptionDispatchInfo.Capture(_ownerFailure).Throw();
            }

            return Volatile.Read(ref _acquired) != 0;
        }
        finally
        {
            lock (_gate)
            {
                _inflightAcquirers--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    public void Release()
    {
        Thread? ownerThread;
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            if (_releaseInitiated)
            {
                while (!_released)
                {
                    Monitor.Wait(_gate);
                }
                return;
            }

            _releaseInitiated = true;
            if (_inflightAcquirers != 0)
            {
                _testHooks?.ReleaseWaitingForAcquirers?.Invoke();
            }
            while (_inflightAcquirers != 0)
            {
                Monitor.Wait(_gate);
            }
            ownerThread = _ownerThread;
            _releaseRequested.Set();
        }

        ownerThread?.Join();
        _acquisitionCompleted.Dispose();
        _releaseRequested.Dispose();
        lock (_gate)
        {
            _released = true;
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose() => Release();

    public static string CreateScopedName(string applicationIdentity, string userIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentity);

        var material = Encoding.UTF8.GetBytes($"{applicationIdentity}\n{userIdentity}");
        var digest = Convert.ToHexString(SHA256.HashData(material));
        return $@"Local\ApexLab.{digest}";
    }

    private static string GetCurrentUserIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        return string.IsNullOrWhiteSpace(sid)
            ? $@"{Environment.UserDomainName}\{Environment.UserName}"
            : sid;
    }

    private void OwnMutex()
    {
        try
        {
            using var mutex = new Mutex(initiallyOwned: false, _mutexName);
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            Volatile.Write(ref _acquired, acquired ? 1 : 0);
            _testHooks?.BeforePublishingAcquisition?.Invoke();
            _acquisitionCompleted.Set();
            if (!acquired)
            {
                return;
            }

            _releaseRequested.Wait();
            mutex.ReleaseMutex();
            Volatile.Write(ref _acquired, 0);
        }
        catch (Exception exception)
        {
            _ownerFailure = exception;
            _acquisitionCompleted.Set();
        }
    }
}
