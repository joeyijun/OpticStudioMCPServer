using System.ComponentModel;
using System.Diagnostics;
using ZemaxMCP.Server.Tooling;
using ZOSAPI.Tools;
using ZOSAPI.Tools.Optimization;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class HammerOptimizationTool
{
    private readonly IZemaxSession _session;

    public HammerOptimizationTool(IZemaxSession session) => _session = session;

    public record HammerResult(
        bool Success,
        string? Error,
        double InitialMerit,
        double FinalMerit,
        double Improvement,
        string Algorithm,
        double RuntimeSeconds,
        int Variables,
        int Improvements,
        string TerminationReason
    );

    [ZemaxTool(Name = "zemax_hammer")]
    [Description("Run Hammer Optimization with the official AutomaticOptimization/TargetRunTimeM settings and a separate finite MCP wall-clock timeout. Timeout or caller cancellation always cancels and drains Hammer before Close.")]
    public async Task<HammerResult> ExecuteAsync(
        [Description("Optimization algorithm: DLS or Orthogonal")] string algorithm = "DLS",
        [Description("CPU cores: 0 uses MaxCores; otherwise the value must be between 1 and MaxCores.")] int cores = 0,
        [Description("OpticStudio Hammer target runtime in minutes; must be finite and > 0.")] double targetRuntimeMinutes = 5.0,
        [Description("MCP wall-clock timeout in seconds; must be finite and > 0. Reaching it cancels Hammer and returns the best merit found so far.")] double timeoutSeconds = 120,
        [Description("Set OpticStudio Hammer's AutomaticOptimization option. This is the pre-Hammer automatic local optimization option; it is not a fixed-cycle selector.")] bool automatic = true,
        CancellationToken cancellationToken = default)
    {
        string algorithmName = algorithm?.Trim() ?? string.Empty;
        try
        {
            var algorithmValue = ParseAlgorithm(algorithmName);
            if (cores < 0) throw new ArgumentOutOfRangeException(nameof(cores), "cores must be >= 0.");
            ValidatePositiveFinite(targetRuntimeMinutes, nameof(targetRuntimeMinutes));
            ValidatePositiveFinite(timeoutSeconds, nameof(timeoutSeconds));

            var parameters = new Dictionary<string, object?>
            {
                ["algorithm"] = algorithmName,
                ["cores"] = cores,
                ["targetRuntimeMinutes"] = targetRuntimeMinutes,
                ["timeoutSeconds"] = timeoutSeconds,
                ["automatic"] = automatic
            };

            return await _session.ExecuteAsync("Hammer", parameters, system =>
            {
                var hammer = system.Tools?.OpenHammerOptimization()
                    ?? throw new InvalidOperationException("Failed to open Hammer Optimization tool.");
                try
                {
                    hammer.Algorithm = algorithmValue;
                    if (cores > hammer.MaxCores)
                        throw new ArgumentOutOfRangeException(nameof(cores), $"cores={cores} exceeds this OpticStudio instance's MaxCores ({hammer.MaxCores}).");
                    hammer.NumberOfCores = cores == 0 ? hammer.MaxCores : cores;
                    hammer.AutomaticOptimization = automatic;
                    hammer.TargetRunTimeM = targetRuntimeMinutes;
                    if (!hammer.IsValid)
                        throw new InvalidOperationException("Hammer Optimization settings are not valid for the current system.");

                    double initialMerit = hammer.InitialMeritFunction;
                    ValidateFiniteResult(initialMerit, "initial merit function");
                    int variables = hammer.Variables;
                    var stopwatch = Stopwatch.StartNew();
                    int improvements = 0;
                    double bestObservedMerit = initialMerit;

                    string terminationReason = RunHammer(
                        hammer,
                        timeoutSeconds,
                        cancellationToken,
                        stopwatch,
                        merit =>
                        {
                            if (double.IsNaN(merit) || double.IsInfinity(merit))
                                throw new InvalidOperationException("Hammer Optimization reported a non-finite current merit function.");
                            double tolerance = Math.Max(1e-12, Math.Abs(bestObservedMerit) * 1e-12);
                            if (merit < bestObservedMerit - tolerance)
                            {
                                bestObservedMerit = merit;
                                improvements++;
                            }
                        });
                    stopwatch.Stop();

                    if (terminationReason == "Completed" && !hammer.Succeeded)
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(hammer.ErrorMessage)
                            ? "Hammer Optimization completed without success."
                            : hammer.ErrorMessage);

                    double finalMerit = hammer.CurrentMeritFunction;
                    ValidateFiniteResult(finalMerit, "final merit function");

                    return new HammerResult(
                        true,
                        null,
                        initialMerit,
                        finalMerit,
                        initialMerit - finalMerit,
                        algorithmValue.ToString(),
                        stopwatch.Elapsed.TotalSeconds,
                        variables,
                        improvements,
                        terminationReason);
                }
                finally
                {
                    CancelIfStillRunning(hammer);
                    hammer.Close();
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HammerResult(false, ex.Message, 0, 0, 0, algorithmName, 0, 0, 0, "Error");
        }
    }

    private static string RunHammer(
        IHammerOptimization hammer,
        double timeoutSeconds,
        CancellationToken cancellationToken,
        Stopwatch stopwatch,
        Action<double> observeMerit)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!hammer.Run())
            throw new InvalidOperationException("OpticStudio failed to start Hammer Optimization.");

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelAndDrain(hammer);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (stopwatch.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                CancelAndDrain(hammer);
                return "TimedOut";
            }

            double remaining = Math.Max(0.01, timeoutSeconds - stopwatch.Elapsed.TotalSeconds);
            var status = hammer.WaitWithTimeout(Math.Min(0.5, remaining));
            observeMerit(hammer.CurrentMeritFunction);
            switch (status)
            {
                case RunStatus.Completed:
                    return "Completed";
                case RunStatus.TimedOut:
                    continue;
                case RunStatus.FailedToStart:
                    throw new InvalidOperationException("Hammer Optimization failed to start.");
                case RunStatus.InvalidTimeout:
                    throw new InvalidOperationException("OpticStudio rejected the Hammer polling timeout.");
                default:
                    throw new InvalidOperationException($"Unexpected Hammer Optimization run status: {status}.");
            }
        }
    }

    private static OptimizationAlgorithm ParseAlgorithm(string algorithm) => algorithm.ToUpperInvariant() switch
    {
        "DLS" => OptimizationAlgorithm.DampedLeastSquares,
        "ORTHOGONAL" => OptimizationAlgorithm.OrthogonalDescent,
        _ => throw new ArgumentException("algorithm must be 'DLS' or 'Orthogonal'.", nameof(algorithm))
    };

    private static void ValidatePositiveFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and > 0.");
    }

    private static void ValidateFiniteResult(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException($"Hammer Optimization reported a non-finite {label}.");
    }

    private static void CancelAndDrain(IHammerOptimization hammer)
    {
        if (hammer.IsRunning && hammer.CanCancel && !hammer.Cancel())
            throw new InvalidOperationException("OpticStudio rejected cancellation of Hammer Optimization.");
        if (hammer.IsRunning && !hammer.WaitForCompletion())
            throw new InvalidOperationException("Hammer Optimization did not drain after cancellation.");
    }

    private static void CancelIfStillRunning(IHammerOptimization hammer)
    {
        if (!hammer.IsRunning) return;
        try
        {
            if (hammer.CanCancel) hammer.Cancel();
            hammer.WaitForCompletion();
        }
        catch { }
    }
}
