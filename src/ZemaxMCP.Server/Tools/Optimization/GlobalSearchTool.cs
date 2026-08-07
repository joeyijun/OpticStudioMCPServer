using System.ComponentModel;
using System.Diagnostics;
using ZemaxMCP.Server.Tooling;
using ZOSAPI.Tools;
using ZOSAPI.Tools.Optimization;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class GlobalSearchTool
{
    private readonly IZemaxSession _session;
    private readonly McpJobManager _jobs;

    public GlobalSearchTool(IZemaxSession session, McpJobManager jobs)
    {
        _session = session;
        _jobs = jobs;
    }

    public record GlobalSearchResult(
        bool Success,
        string? Error,
        double InitialMerit,
        double BestMerit,
        double Improvement,
        int SolutionsSaved,
        string Algorithm,
        double RuntimeSeconds,
        string TerminationReason,
        string? JobId = null,
        string? JobState = null
    );

    [ZemaxTool(Name = "zemax_global_search")]
    [Description("Run Global Optimization with strict settings and explicit timeout/cancellation cleanup. A timeout always cancels and drains the Zemax tool before results are read or the tool is closed.")]
    public async Task<GlobalSearchResult> ExecuteAsync(
        [Description("Optimization algorithm: DLS or Orthogonal")] string algorithm = "DLS",
        [Description("CPU cores: 0 uses MaxCores; otherwise the value must be between 1 and MaxCores.")] int cores = 0,
        [Description("Number of solutions to retain: exactly 10, 20, 50, or 100.")] int solutionsToSave = 10,
        [Description("Wall-clock runtime in seconds. 0 means no wall-clock limit and is permitted only for a background job, which must then be cancelled explicitly.")] double timeoutSeconds = 60,
        [Description("Queue this long operation and return a job id immediately (recommended).")] bool runInBackground = true,
        CancellationToken cancellationToken = default)
    {
        string algorithmName = algorithm?.Trim() ?? string.Empty;
        try
        {
            ValidatePublicInputs(algorithmName, cores, solutionsToSave, timeoutSeconds, runInBackground);
            if (!runInBackground)
                return await ExecuteCoreAsync(algorithmName, cores, solutionsToSave, timeoutSeconds, cancellationToken);

            var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds + 30) : (TimeSpan?)null;
            var job = _jobs.Enqueue("zemax_global_search", async context =>
            {
                context.ReportProgress(0, "Waiting for the ZOS-API job slot.");
                var result = await ExecuteCoreAsync(algorithmName, cores, solutionsToSave, timeoutSeconds, context.CancellationToken);
                if (!result.Success) throw new InvalidOperationException(result.Error ?? "Global search failed.");
                context.SetResult(result);
                context.ReportProgress(1, result.TerminationReason);
            }, timeout);

            return new GlobalSearchResult(true, null, 0, 0, 0, 0, algorithmName, 0,
                "Queued. Use zemax_job_status or zemax_job_cancel.", job.JobId, job.State.ToString());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GlobalSearchResult(false, ex.Message, 0, 0, 0, 0, algorithmName, 0, "Error");
        }
    }

    private async Task<GlobalSearchResult> ExecuteCoreAsync(
        string algorithm, int cores, int solutionsToSave, double timeoutSeconds, CancellationToken cancellationToken)
    {
        var algorithmValue = ParseAlgorithm(algorithm);
        var saveCount = ParseSaveCount(solutionsToSave);
        var parameters = new Dictionary<string, object?>
        {
            ["algorithm"] = algorithm,
            ["cores"] = cores,
            ["solutionsToSave"] = solutionsToSave,
            ["timeoutSeconds"] = timeoutSeconds
        };

        cancellationToken.ThrowIfCancellationRequested();
        return await _session.ExecuteAsync("GlobalSearch", parameters, system =>
        {
            var globalOpt = system.Tools?.OpenGlobalOptimization()
                ?? throw new InvalidOperationException("Failed to open Global Optimization tool.");
            try
            {
                globalOpt.Algorithm = algorithmValue;
                if (cores > globalOpt.MaxCores)
                    throw new ArgumentOutOfRangeException(nameof(cores), $"cores={cores} exceeds this OpticStudio instance's MaxCores ({globalOpt.MaxCores}).");
                globalOpt.NumberOfCores = cores == 0 ? globalOpt.MaxCores : cores;
                globalOpt.NumberToSave = saveCount;
                if (!globalOpt.IsValid)
                    throw new InvalidOperationException("Global Optimization settings are not valid for the current system.");

                double initialMerit = globalOpt.InitialMeritFunction;
                ValidateFinite(initialMerit, "initial merit function");
                var stopwatch = Stopwatch.StartNew();
                string terminationReason = RunUntilCompletionTimeoutOrCancellation(globalOpt, timeoutSeconds, cancellationToken, stopwatch);
                stopwatch.Stop();

                if (terminationReason == "Completed" && !globalOpt.Succeeded)
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(globalOpt.ErrorMessage)
                        ? "Global Optimization completed without success."
                        : globalOpt.ErrorMessage);

                var merits = new List<double>();
                for (int i = 1; i <= solutionsToSave; i++)
                {
                    double merit = globalOpt.CurrentMeritFunction(i);
                    if (double.IsNaN(merit) || double.IsInfinity(merit) || merit >= double.MaxValue)
                        break;
                    merits.Add(merit);
                }
                if (merits.Count == 0)
                    throw new InvalidOperationException($"Global Optimization ended with '{terminationReason}' but returned no valid saved solution.");

                double bestMerit = merits.Min();
                return new GlobalSearchResult(
                    true,
                    null,
                    initialMerit,
                    bestMerit,
                    initialMerit - bestMerit,
                    merits.Count,
                    algorithmValue.ToString(),
                    stopwatch.Elapsed.TotalSeconds,
                    terminationReason);
            }
            finally
            {
                CancelIfStillRunning(globalOpt);
                globalOpt.Close();
            }
        }, cancellationToken);
    }

    private static string RunUntilCompletionTimeoutOrCancellation(
        IGlobalOptimization tool, double timeoutSeconds, CancellationToken cancellationToken, Stopwatch stopwatch)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!tool.Run())
            throw new InvalidOperationException("OpticStudio failed to start Global Optimization.");

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelAndDrain(tool, "Global Optimization");
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (timeoutSeconds > 0 && stopwatch.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                CancelAndDrain(tool, "Global Optimization");
                return "TimedOut";
            }

            double remaining = timeoutSeconds > 0 ? Math.Max(0.01, timeoutSeconds - stopwatch.Elapsed.TotalSeconds) : 0.25;
            var status = tool.WaitWithTimeout(Math.Min(0.25, remaining));
            switch (status)
            {
                case RunStatus.Completed:
                    return "Completed";
                case RunStatus.TimedOut:
                    continue;
                case RunStatus.FailedToStart:
                    throw new InvalidOperationException("Global Optimization failed to start.");
                case RunStatus.InvalidTimeout:
                    throw new InvalidOperationException("OpticStudio rejected the Global Optimization polling timeout.");
                default:
                    throw new InvalidOperationException($"Unexpected Global Optimization run status: {status}.");
            }
        }
    }

    private static void ValidatePublicInputs(string algorithm, int cores, int solutionsToSave, double timeoutSeconds, bool runInBackground)
    {
        ParseAlgorithm(algorithm);
        ParseSaveCount(solutionsToSave);
        if (cores < 0) throw new ArgumentOutOfRangeException(nameof(cores), "cores must be >= 0.");
        if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "timeoutSeconds must be finite and >= 0.");
        if (!runInBackground && timeoutSeconds == 0)
            throw new ArgumentException("timeoutSeconds=0 is allowed only when runInBackground=true; a foreground MCP request must have a finite wall-clock bound.");
    }

    private static OptimizationAlgorithm ParseAlgorithm(string algorithm) => algorithm.ToUpperInvariant() switch
    {
        "DLS" => OptimizationAlgorithm.DampedLeastSquares,
        "ORTHOGONAL" => OptimizationAlgorithm.OrthogonalDescent,
        _ => throw new ArgumentException("algorithm must be 'DLS' or 'Orthogonal'.", nameof(algorithm))
    };

    private static OptimizationSaveCount ParseSaveCount(int solutionsToSave) => solutionsToSave switch
    {
        10 => OptimizationSaveCount.Save_10,
        20 => OptimizationSaveCount.Save_20,
        50 => OptimizationSaveCount.Save_50,
        100 => OptimizationSaveCount.Save_100,
        _ => throw new ArgumentOutOfRangeException(nameof(solutionsToSave), "solutionsToSave must be exactly 10, 20, 50, or 100.")
    };

    private static void CancelAndDrain(IGlobalOptimization tool, string label)
    {
        if (tool.IsRunning && tool.CanCancel && !tool.Cancel())
            throw new InvalidOperationException($"OpticStudio rejected cancellation of {label}.");
        if (tool.IsRunning && !tool.WaitForCompletion())
            throw new InvalidOperationException($"{label} did not drain after cancellation.");
    }

    private static void CancelIfStillRunning(IGlobalOptimization tool)
    {
        if (!tool.IsRunning) return;
        try
        {
            if (tool.CanCancel) tool.Cancel();
            tool.WaitForCompletion();
        }
        catch { }
    }

    private static void ValidateFinite(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException($"Global Optimization reported a non-finite {label}.");
    }
}
