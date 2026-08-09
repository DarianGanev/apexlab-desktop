namespace ApexLab.App.Lifecycle;

public interface ISingleInstanceLease : IDisposable
{
    bool TryAcquire();

    void Release();
}
