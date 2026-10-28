using System;
using System.Threading;

namespace NecoArcDesktopPet.Product;

// Owned and disposed on the Product entry STA thread. Local scopes the stable
// name to the Windows session; future Inno Setup AppMutex must use this exact name.
internal sealed class ProductSingleInstance : IDisposable
{
    internal const string MutexName = @"Local\NecoArcDesktopPet.SourceOnly.Product";
    private Mutex? mutex;

    private ProductSingleInstance(Mutex mutex) => this.mutex = mutex;

    internal static ProductSingleInstance? TryAcquire()
    {
        var handle = new Mutex(false, MutexName);
        try
        {
            bool acquired;
            try { acquired = handle.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new ProductSingleInstance(handle);
            handle.Dispose(); // A secondary never releases the primary's ownership.
            return null;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var handle = mutex;
        if (handle == null) return;
        mutex = null;
        try { handle.ReleaseMutex(); }
        finally { handle.Dispose(); }
    }
}
