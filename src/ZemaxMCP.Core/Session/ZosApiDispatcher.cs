namespace ZemaxMCP.Core.Session;

/// <summary>
/// Serializes all ZOS-API/COM calls onto one long-lived STA. Pending work is
/// bounded and cancellable without waiting behind an unresponsive COM call.
/// An already-running COM invocation cannot be preempted in process; Host
/// generation hard-recovery remains the authority for that scenario.
/// </summary>
internal sealed class ZosApiDispatcher : IDisposable
{
    internal const int DefaultMaxPending = 64;

    private readonly object _gate = new();
    private readonly LinkedList<WorkItem> _pending = new();
    private readonly Thread _thread;
    private readonly int _maxPending;
    private bool _disposed;

    public ZosApiDispatcher(int maxPending = DefaultMaxPending)
    {
        if (maxPending < 1 || maxPending > 4096)
            throw new ArgumentOutOfRangeException(nameof(maxPending));
        _maxPending = maxPending;
        _thread = new Thread(Run) { IsBackground = true, Name = "Zemax ZOS-API STA" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;
    public ApartmentState ApartmentState => _thread.GetApartmentState();
    public int PendingCount { get { lock (_gate) return _pending.Count; } }
    internal Task<int> GetExecutingThreadIdAsync() => InvokeAsync(() => Thread.CurrentThread.ManagedThreadId);

    public Task<T> InvokeAsync<T>(Func<T> operation, CancellationToken cancellationToken = default)
    {
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<T>(cancellationToken);

        var item = new WorkItem<T>(operation, cancellationToken);
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ZosApiDispatcher));
            if (_pending.Count >= _maxPending)
                throw new InvalidOperationException(
                    "The bounded ZOS-API STA queue is full. Retry after an existing optical operation completes.");
            item.Node = _pending.AddLast(item);
            Monitor.Pulse(_gate);
        }

        if (cancellationToken.CanBeCanceled)
        {
            // Handles cancellation even if the COM STA is blocked indefinitely.
            // An in-flight operation is never aborted on this thread: only
            // queued items can be removed. Continuation disposes registration.
            var registration = cancellationToken.Register(() => CancelPending(item));
            _ = item.Completed.ContinueWith(_ => registration.Dispose(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return item.Result;
    }

    public Task InvokeAsync(Action operation, CancellationToken cancellationToken = default) =>
        InvokeAsync(() => { operation(); return true; }, cancellationToken);

    private void CancelPending(WorkItem item)
    {
        var removed = false;
        lock (_gate)
        {
            if (item.Node?.List == _pending)
            {
                _pending.Remove(item.Node);
                item.Node = null;
                removed = true;
            }
        }
        if (removed) item.Cancel();
    }

    private void Run()
    {
        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                while (_pending.Count == 0 && !_disposed) Monitor.Wait(_gate);
                if (_disposed && _pending.Count == 0) return;
                item = _pending.First!.Value;
                _pending.RemoveFirst();
                item.Node = null;
            }
            // Do not hold the queue lock across any COM call.
            item.Execute();
        }
    }

    public void Dispose()
    {
        WorkItem[] rejected;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            rejected = _pending.ToArray();
            _pending.Clear();
            foreach (var item in rejected) item.Node = null;
            Monitor.PulseAll(_gate);
        }
        foreach (var item in rejected) item.FailDisposed();

        // An already-running COM call might hang. Never destroy synchronization
        // primitives that the STA may still use after a timed-out join.
        // The Worker is process-isolated; Host hard recovery kills that process.
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(10));
    }

    private abstract class WorkItem
    {
        internal LinkedListNode<WorkItem>? Node;
        internal abstract Task Completed { get; }
        internal abstract void Execute();
        internal abstract void Cancel();
        internal abstract void FailDisposed();
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<T> _action;
        private readonly CancellationToken _token;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal WorkItem(Func<T> action, CancellationToken token) { _action = action; _token = token; }
        internal Task<T> Result => _completion.Task;
        internal override Task Completed => _completion.Task;
        internal override void Execute()
        {
            if (_token.IsCancellationRequested) { Cancel(); return; }
            try { _completion.TrySetResult(_action()); }
            catch (Exception ex) { _completion.TrySetException(ex); }
        }
        internal override void Cancel()
        {
            if (_token.CanBeCanceled && _token.IsCancellationRequested)
                _completion.TrySetCanceled(_token);
            else _completion.TrySetCanceled();
        }
        internal override void FailDisposed() =>
            _completion.TrySetException(new ObjectDisposedException(nameof(ZosApiDispatcher)));
    }
}
