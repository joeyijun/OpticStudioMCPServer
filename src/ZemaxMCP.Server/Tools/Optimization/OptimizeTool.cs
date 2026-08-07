using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Tools;
using ZOSAPI.Tools.Optimization;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class OptimizeTool
{
    private readonly IZemaxSession _session;

    public OptimizeTool(IZemaxSession session) => _session = session;

    public record OptimizeResult(
        bool Success,
        string? Error,
        double InitialMerit,
        double FinalMerit,
        double Improvement,
        int CyclesCompleted,
        string Algorithm,
        string TerminationReason
    );

    [ZemaxTool(Name = "zemax_optimize")]
    [Description("Run Zemax Local Optimization with a documented finite/automatic cycle setting. The call is cancellable; unsupported cycle counts are rejected instead of being silently rounded or converted to Infinite.")]
    public async Task<OptimizeResult> ExecuteAsync(
        [Description("Optimization algorithm: DLS or Orthogonal")] string algorithm = "DLS",
        [Description("Zemax cycle setting: 0=Automatic, or exactly 1, 5, 10, or 50 fixed cycles. Infinite is intentionally not exposed by this synchronous MCP tool.")] int cycles = 0,
        CancellationToken cancellationToken = default)
    {
        string algorithmName = algorithm?.Trim() ?? string.Empty;
        try
        {
            var algorithmValue = ParseAlgorithm(algorithmName);
            var cycleValue = ParseCycles(cycles);
            var parameters = new Dictionary<string, object?>
            {
                ["algorithm"] = algorithmName,
                ["cycles"] = cycles
            };

            return await _session.ExecuteAsync("Optimize", parameters, system =>
            {
                var optimizer = system.Tools?.OpenLocalOptimization()
                    ?? throw new InvalidOperationException("Failed to open Local Optimization tool.");
                try
                {
                    optimizer.Algorithm = algorithmValue;
                    optimizer.Cycles = cycleValue;
                    if (!optimizer.IsValid)
                        throw new InvalidOperationException("Local Optimization settings are not valid for the current system.");

                    double initialMerit = optimizer.InitialMeritFunction;
                    if (!double.IsFinite(initialMerit))
                        throw new InvalidOperationException("Local Optimization reported a non-finite initial merit function.");

                    RunToCompletionOrCancellation(optimizer, cancellationToken);
                    if (!optimizer.Succeeded)
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(optimizer.ErrorMessage)
                            ? "Local Optimization did not succeed."
                            : optimizer.ErrorMessage);

                    double finalMerit = optimizer.CurrentMeritFunction;
                    if (!double.IsFinite(finalMerit))
                        throw new InvalidOperationException("Local Optimization reported a non-finite final merit function.");

                    string terminationReason = Math.Abs(finalMerit - initialMerit) < 1e-12
                        ? "CompletedNoMeritChange"
                        : "Completed";

                    return new OptimizeResult(
                        true,
                        null,
                        initialMerit,
                        finalMerit,
                        initialMerit - finalMerit,
                        cycles == 0 ? -1 : cycles,
                        algorithmValue.ToString(),
                        terminationReason);
                }
                finally
                {
                    CancelIfStillRunning(optimizer);
                    optimizer.Close();
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OptimizeResult(false, ex.Message, 0, 0, 0, 0, algorithmName, "Error");
        }
    }

    private static OptimizationAlgorithm ParseAlgorithm(string algorithm) => algorithm.ToUpperInvariant() switch
    {
        "DLS" => OptimizationAlgorithm.DampedLeastSquares,
        "ORTHOGONAL" => OptimizationAlgorithm.OrthogonalDescent,
        _ => throw new ArgumentException("algorithm must be 'DLS' or 'Orthogonal'.", nameof(algorithm))
    };

    private static OptimizationCycles ParseCycles(int cycles) => cycles switch
    {
        0 => OptimizationCycles.Automatic,
        1 => OptimizationCycles.Fixed_1_Cycle,
        5 => OptimizationCycles.Fixed_5_Cycles,
        10 => OptimizationCycles.Fixed_10_Cycles,
        50 => OptimizationCycles.Fixed_50_Cycles,
        _ => throw new ArgumentOutOfRangeException(nameof(cycles), "cycles must be exactly 0, 1, 5, 10, or 50.")
    };

    private static void RunToCompletionOrCancellation(ILocalOptimization optimizer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!optimizer.Run())
            throw new InvalidOperationException("OpticStudio failed to start Local Optimization.");

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelAndDrain(optimizer);
                cancellationToken.ThrowIfCancellationRequested();
            }

            var status = optimizer.WaitWithTimeout(0.25);
            switch (status)
            {
                case RunStatus.Completed:
                    return;
                case RunStatus.TimedOut:
                    continue;
                case RunStatus.FailedToStart:
                    throw new InvalidOperationException("Local Optimization failed to start.");
                case RunStatus.InvalidTimeout:
                    throw new InvalidOperationException("OpticStudio rejected the Local Optimization polling timeout.");
                default:
                    throw new InvalidOperationException($"Unexpected Local Optimization run status: {status}.");
            }
        }
    }

    private static void CancelAndDrain(ILocalOptimization optimizer)
    {
        if (optimizer.IsRunning && optimizer.CanCancel && !optimizer.Cancel())
            throw new InvalidOperationException("OpticStudio rejected cancellation of Local Optimization.");
        if (optimizer.IsRunning && !optimizer.WaitForCompletion())
            throw new InvalidOperationException("Local Optimization did not drain after cancellation.");
    }

    private static void CancelIfStillRunning(ILocalOptimization optimizer)
    {
        if (!optimizer.IsRunning) return;
        try
        {
            if (optimizer.CanCancel) optimizer.Cancel();
            optimizer.WaitForCompletion();
        }
        catch { }
    }
}
