using System.Collections.Concurrent;

namespace ZemaxMCP.Core.Session;

/// <summary>Serializes every ZOS-API/COM call onto one long-lived STA thread.</summary>
internal sealed class ZosApiDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public ZosApiDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Zemax ZOS-API STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;
    public ApartmentState ApartmentState => _thread.GetApartmentState();
    internal Task<int> GetExecutingThreadIdAsync() => InvokeAsync(() => Thread.CurrentThread.ManagedThreadId);

    public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ZosApiDispatcher));
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled();
                return;
            }
            try { completion.TrySetResult(operation()); }
            catch (Exception ex) { completion.TrySetException(ex); }
        });
        return completion.Task;
    }

    public Task InvokeAsync(Action operation, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => { operation(); return true; }, cancellationToken);

    private void Run()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(10));
        _queue.Dispose();
    }
}
