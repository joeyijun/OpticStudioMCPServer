namespace ZemaxMCP.Core.Services.ConstrainedOptimization;

/// <summary>
/// Persists multistart optimization state between calls to support resume,
/// progress reporting, cancellation, and auxiliary save diagnostics.
/// </summary>
public class MultistartState
{
    public string? SaveFolder { get; set; }
    public int TotalTrialsRun { get; set; }
    public int TotalTrialsAccepted { get; set; }
    public double InitialMerit { get; set; }
    public double BestMerit { get; set; }
    public bool InitialLmDone { get; set; }
    public int SaveCount { get; set; }

    private volatile int _currentTrial;
    private volatile int _maxTrials;
    private volatile bool _isRunning;
    private volatile bool _isInInitialLm;
    private volatile string? _errorMessage;
    private volatile string? _lastWarning;
    private volatile int _initialLmIteration;
    private volatile int _initialLmMaxIterations;
    private double _initialLmMerit;
    private CancellationTokenSource? _cts;
    private Task? _backgroundTask;
    private readonly object _lock = new();

    public int CurrentTrial => _currentTrial;
    public int MaxTrials => _maxTrials;
    public bool IsRunning => _isRunning;
    public bool IsInInitialLm => _isInInitialLm;
    public string? ErrorMessage => _errorMessage;
    public string? LastWarning => _lastWarning;
    public int InitialLmIteration => _initialLmIteration;
    public int InitialLmMaxIterations => _initialLmMaxIterations;
    public double InitialLmMerit { get { lock (_lock) return _initialLmMerit; } }

    public bool HasState => InitialLmDone;

    public void Reset()
    {
        SaveFolder = null;
        TotalTrialsRun = 0;
        TotalTrialsAccepted = 0;
        InitialMerit = 0;
        BestMerit = 0;
        InitialLmDone = false;
        SaveCount = 0;
        _currentTrial = 0;
        _maxTrials = 0;
        _isRunning = false;
        _isInInitialLm = false;
        _errorMessage = null;
        _lastWarning = null;
        _initialLmIteration = 0;
        _initialLmMaxIterations = 0;
        _initialLmMerit = 0;
    }

    public void UpdateProgress(int currentTrial, int maxTrials, double bestMerit, int trialsAccepted)
    {
        _currentTrial = currentTrial;
        _maxTrials = maxTrials;
        BestMerit = bestMerit;
        TotalTrialsAccepted = trialsAccepted;
    }

    public void SetRunning(int maxTrials)
    {
        _isRunning = true;
        _isInInitialLm = true;
        _maxTrials = maxTrials;
        _currentTrial = 0;
        _errorMessage = null;
        _lastWarning = null;
    }

    public void UpdateInitialLmProgress(int iteration, int maxIterations, double merit)
    {
        _initialLmIteration = iteration;
        _initialLmMaxIterations = maxIterations;
        lock (_lock) _initialLmMerit = merit;
    }

    public void SetInitialLmComplete() => _isInInitialLm = false;

    public void SetCompleted(string? error = null)
    {
        _isRunning = false;
        _isInInitialLm = false;
        _errorMessage = error;
    }

    public void SetWarning(string warning) => _lastWarning = warning;

    public void ClearWarning() => _lastWarning = null;

    public CancellationToken CreateCancellationToken(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return _cts.Token;
        }
    }

    public void RequestCancellation()
    {
        lock (_lock) _cts?.Cancel();
    }

    public void SetBackgroundTask(Task task) => _backgroundTask = task;

    public Task? BackgroundTask => _backgroundTask;
}
