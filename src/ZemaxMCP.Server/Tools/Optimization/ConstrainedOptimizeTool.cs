using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class ConstrainedOptimizeTool
{
    private readonly IZemaxSession _session;
    private readonly ConstraintStore _constraintStore;

    public ConstrainedOptimizeTool(IZemaxSession session, ConstraintStore constraintStore)
    {
        _session = session;
        _constraintStore = constraintStore;
    }

    public record ConstrainedOptimizeResult(
        bool Success,
        string? Error,
        double InitialMerit,
        double FinalMerit,
        int Iterations,
        int Restarts,
        string Message
    );

    [ZemaxTool(Name = "zemax_constrained_optimize")]
    [Description("Custom MCP-implemented bound-constrained Levenberg-Marquardt optimizer with optional Broyden updates. Invalid numerical settings are rejected before the HighImpact session operation, and cancellation restores the last accepted design before propagating to the MCP/job layer.")]
    public async Task<ConstrainedOptimizeResult> ExecuteAsync(
        [Description("Maximum LM iterations; must be > 0.")] int maxIterations = 200,
        [Description("Initial damping parameter mu; must be finite and > 0.")] double initialMu = 1e-3,
        [Description("Finite-difference relative step; must be finite and > 0.")] double delta = 1e-7,
        [Description("Use Broyden rank-1 Jacobian updates to reduce evaluations.")] bool useBroydenUpdate = true,
        [Description("Maximum fresh-Jacobian restarts; must be >= 0. Ignored by the core algorithm when Broyden updates are disabled.")] int maxRestarts = 2,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (maxIterations <= 0) throw new ArgumentOutOfRangeException(nameof(maxIterations), "maxIterations must be > 0.");
            ValidatePositiveFinite(initialMu, nameof(initialMu));
            ValidatePositiveFinite(delta, nameof(delta));
            if (maxRestarts < 0) throw new ArgumentOutOfRangeException(nameof(maxRestarts), "maxRestarts must be >= 0.");
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new Dictionary<string, object?>
            {
                ["maxIterations"] = maxIterations,
                ["initialMu"] = initialMu,
                ["delta"] = delta,
                ["useBroydenUpdate"] = useBroydenUpdate,
                ["maxRestarts"] = maxRestarts
            };

            return await _session.ExecuteAsync("ConstrainedOptimize", parameters, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scanner = new VariableScanner();
                var meritReader = new MeritFunctionReader();
                var lmOptimizer = new LMOptimizer(meritReader);
                var variables = scanner.ScanVariables(system, cancellationToken);
                _constraintStore.ApplyConstraints(variables);
                cancellationToken.ThrowIfCancellationRequested();

                var lmResult = lmOptimizer.Optimize(
                    system,
                    variables,
                    maxIterations,
                    initialMu,
                    delta,
                    useBroydenUpdate: useBroydenUpdate,
                    maxRestarts: maxRestarts,
                    cancellationToken: cancellationToken);

                return new ConstrainedOptimizeResult(
                    lmResult.Success,
                    lmResult.Success ? null : lmResult.Message,
                    lmResult.InitialMerit,
                    lmResult.FinalMerit,
                    lmResult.Iterations,
                    lmResult.Restarts,
                    lmResult.Message);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ConstrainedOptimizeResult(false, ex.Message, 0, 0, 0, 0, $"Error: {ex.Message}");
        }
    }

    private static void ValidatePositiveFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and > 0.");
    }
}
