using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class MultistartOptimizeTool
{
    private readonly IZemaxSession _session;
    private readonly ConstraintStore _constraintStore;
    private readonly MultistartState _multistartState;
    private readonly McpJobManager _jobs;

    public MultistartOptimizeTool(IZemaxSession session, ConstraintStore constraintStore, MultistartState multistartState, McpJobManager jobs)
    {
        _session = session;
        _constraintStore = constraintStore;
        _multistartState = multistartState;
        _jobs = jobs;
    }

    public record MultistartOptimizeResult(
        bool Success,
        string? Error,
        string Message,
        string? JobId = null);

    [ZemaxTool(Name = "zemax_multistart_optimize")]
    [Description("Start non-blocking custom multistart optimization. Cancellation propagates through variable/material discovery and every LM/Jacobian evaluation; cancellation restores the best accepted design. Checkpoint saves use CopySystem so the active lens file identity is not changed.")]
    public MultistartOptimizeResult Execute(
        [Description("Number of random restart trials; must be > 0.")] int maxTrials = 100,
        [Description("LM iterations per trial; must be > 0.")] int lmIterationsPerTrial = 50,
        [Description("Full LM iterations before trials begin; must be > 0.")] int initialLmIterations = 200,
        [Description("Randomization percentage of the bound/current range; must be finite in 0..100.")] double randomizationPercent = 5.0,
        [Description("Initial LM damping parameter; must be finite and > 0.")] double initialMu = 1e-3,
        [Description("Finite-difference relative step; must be finite and > 0.")] double delta = 1e-7,
        [Description("Use Broyden rank-1 Jacobian updates.")] bool useBroydenUpdate = true,
        [Description("Maximum fresh-Jacobian restarts for the initial LM; must be >= 0.")] int maxRestarts = 0,
        [Description("Only randomize variables that have explicit constraints.")] bool constrainedOnly = false,
        [Description("Probability of one glass substitution per trial; must be finite in 0..1.")] double glassSubstitutionProbability = 0.5,
        [Description("Deprecated compatibility parameter. stderr progress logging was retired; only 0 is accepted. Use zemax_multistart_status/job progress instead.")] int progressInterval = 0,
        [Description("Resume counters/design from a previous completed multistart run. Requires existing multistart state; otherwise the request fails.")] bool resume = false)
    {
        try
        {
            if (_multistartState.IsRunning)
            {
                return new MultistartOptimizeResult(false, "Optimization already running",
                    $"Multistart optimization is already running (trial {_multistartState.CurrentTrial}/{_multistartState.MaxTrials}). " +
                    "Use zemax_multistart_status or zemax_multistart_stop to cancel.");
            }

            ValidateInputs(maxTrials, lmIterationsPerTrial, initialLmIterations, randomizationPercent,
                initialMu, delta, maxRestarts, glassSubstitutionProbability, progressInterval);
            if (resume && !_multistartState.HasState)
                throw new InvalidOperationException("resume=true requires a previously completed multistart state; no resumable state exists.");

            bool skipInitialLm = resume;
            if (!skipInitialLm) _multistartState.Reset();

            string saveExtension = ".zos";
            var currentFile = _session.CurrentFilePath;
            if (!string.IsNullOrEmpty(currentFile))
            {
                var extension = Path.GetExtension(currentFile);
                if (extension.Equals(".zmx", StringComparison.OrdinalIgnoreCase) || extension.Equals(".zos", StringComparison.OrdinalIgnoreCase))
                    saveExtension = extension;

                if (string.IsNullOrEmpty(_multistartState.SaveFolder))
                {
                    var dir = Path.GetDirectoryName(currentFile);
                    if (string.IsNullOrWhiteSpace(dir))
                        throw new InvalidOperationException("The current OpticStudio file has no usable parent directory for multistart checkpoints.");
                    var name = Path.GetFileNameWithoutExtension(currentFile);
                    var saveFolder = Path.Combine(dir, $"{name}_multistart");
                    Directory.CreateDirectory(saveFolder);
                    _multistartState.SaveFolder = saveFolder;
                }
            }

            int priorTrialsRun = _multistartState.TotalTrialsRun;
            int priorTrialsAccepted = _multistartState.TotalTrialsAccepted;
            _multistartState.SetRunning(maxTrials);

            var job = _jobs.Enqueue("zemax_multistart_optimize", async context =>
            {
                try
                {
                    var cancellationToken = _multistartState.CreateCancellationToken(context.CancellationToken);
                    context.ReportProgress(0, "Waiting for the ZOS-API job slot.");
                    cancellationToken.ThrowIfCancellationRequested();
                    await _session.ExecuteAsync("MultistartOptimize",
                        new Dictionary<string, object?>
                        {
                            ["maxTrials"] = maxTrials,
                            ["lmIterationsPerTrial"] = lmIterationsPerTrial,
                            ["initialLmIterations"] = initialLmIterations,
                            ["randomizationPercent"] = randomizationPercent,
                            ["initialMu"] = initialMu,
                            ["delta"] = delta,
                            ["useBroydenUpdate"] = useBroydenUpdate,
                            ["maxRestarts"] = maxRestarts,
                            ["constrainedOnly"] = constrainedOnly,
                            ["glassSubstitutionProbability"] = glassSubstitutionProbability,
                            ["resume"] = resume
                        },
                        system =>
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var scanner = new VariableScanner();
                            var meritReader = new MeritFunctionReader();
                            var multistartOptimizer = new MultistartOptimizer(meritReader);
                            var variables = scanner.ScanVariables(system, cancellationToken);
                            _constraintStore.ApplyConstraints(variables);
                            cancellationToken.ThrowIfCancellationRequested();
                            var substituteMaterials = scanner.ScanMaterials(system, cancellationToken)
                                .Where(material => material.SolveType == ZOSAPI.Editors.SolveType.MaterialSubstitute &&
                                                   material.SubstituteGlasses != null &&
                                                   material.SubstituteGlasses.Length > 1)
                                .ToList();
                            cancellationToken.ThrowIfCancellationRequested();

                            Action<int, double> onImprovement = (trial, merit) =>
                            {
                                _multistartState.BestMerit = merit;
                                if (string.IsNullOrEmpty(_multistartState.SaveFolder)) return;

                                var totalTrial = priorTrialsRun + trial;
                                var meritStr = merit.ToString("F6", System.Globalization.CultureInfo.InvariantCulture).Replace(".", "p");
                                var savePath = Path.Combine(_multistartState.SaveFolder, $"best_t{totalTrial}_mf{meritStr}{saveExtension}");
                                try
                                {
                                    SaveSystemCopy(system, savePath);
                                    _multistartState.SaveCount++;
                                    _multistartState.ClearWarning();
                                    try { _constraintStore.SaveToFile(savePath); }
                                    catch (Exception sidecarError)
                                    {
                                        _multistartState.SetWarning($"Optical checkpoint saved, but constraint sidecar failed: {sidecarError.Message}");
                                    }
                                }
                                catch (Exception saveError)
                                {
                                    _multistartState.SetWarning($"Checkpoint save failed at trial {totalTrial}: {saveError.Message}");
                                }
                            };

                            Action<int, int, double, int> onProgress = (trial, total, bestMerit, accepted) =>
                            {
                                _multistartState.UpdateProgress(trial, total, bestMerit, priorTrialsAccepted + accepted);
                                _multistartState.TotalTrialsRun = priorTrialsRun + trial;
                                context.ReportProgress(total > 0 ? (double)trial / total : 0,
                                    $"Trial {trial}/{total}; best merit {bestMerit:F6}.");
                            };

                            Action onInitialLmComplete = () => _multistartState.SetInitialLmComplete();
                            Action<int, int, double> onInitialLmProgress = (iteration, maxIter, merit) =>
                            {
                                _multistartState.UpdateInitialLmProgress(iteration, maxIter, merit);
                                context.ReportProgress(maxIter > 0 ? (double)iteration / maxIter : 0,
                                    $"Initial LM iteration {iteration}/{maxIter}; merit {merit:F6}.");
                            };

                            var msResult = multistartOptimizer.Optimize(
                                system,
                                variables,
                                substituteMaterials,
                                maxTrials,
                                lmIterationsPerTrial,
                                initialLmIterations,
                                randomizationPercent,
                                initialMu,
                                delta,
                                useBroydenUpdate,
                                maxRestarts,
                                constrainedOnly,
                                glassSubstitutionProbability,
                                0,
                                skipInitialLm: skipInitialLm,
                                onImprovement: onImprovement,
                                onProgress: onProgress,
                                onInitialLmComplete: onInitialLmComplete,
                                onInitialLmProgress: onInitialLmProgress,
                                cancellationToken: cancellationToken);

                            if (!msResult.Success)
                                throw new InvalidOperationException(msResult.Message);
                            if (!skipInitialLm) _multistartState.InitialMerit = msResult.InitialMerit;
                            _multistartState.TotalTrialsRun = priorTrialsRun + msResult.TrialsRun;
                            _multistartState.TotalTrialsAccepted = priorTrialsAccepted + msResult.TrialsAccepted;
                            _multistartState.BestMerit = msResult.FinalMerit;
                            _multistartState.InitialLmDone = true;

                            if (!string.IsNullOrEmpty(_multistartState.SaveFolder))
                            {
                                var finalPath = Path.Combine(_multistartState.SaveFolder, "best_current" + saveExtension);
                                try
                                {
                                    SaveSystemCopy(system, finalPath);
                                    try { _constraintStore.SaveToFile(finalPath); }
                                    catch (Exception sidecarError)
                                    {
                                        _multistartState.SetWarning($"Final optical checkpoint saved, but constraint sidecar failed: {sidecarError.Message}");
                                    }
                                }
                                catch (Exception saveError)
                                {
                                    _multistartState.SetWarning($"Final checkpoint save failed: {saveError.Message}");
                                }
                            }

                            _multistartState.SetCompleted();
                            context.SetResult(msResult);
                            context.ReportProgress(1, "Completed.");
                        }, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    _multistartState.SetCompleted("Cancelled by user");
                    throw;
                }
                catch (Exception ex)
                {
                    _multistartState.SetCompleted($"Error: {ex.Message}");
                    throw;
                }
            });

            string resumeNote = skipInitialLm ? " (resuming, skipping initial LM)" : "";
            return new MultistartOptimizeResult(true, null,
                $"Multistart optimization started{resumeNote}: {maxTrials} trials, constrainedOnly={constrainedOnly}. " +
                "Use zemax_multistart_status or zemax_job_status to check progress; use zemax_multistart_stop or zemax_job_cancel to cancel.",
                job.JobId);
        }
        catch (Exception ex)
        {
            return new MultistartOptimizeResult(false, ex.Message, $"Multistart optimization was not started: {ex.Message}");
        }
    }

    private static void SaveSystemCopy(IOpticalSystem system, string savePath)
    {
        var copy = system.CopySystem()
            ?? throw new InvalidOperationException("OpticStudio could not create a system copy for the multistart checkpoint.");
        try
        {
            copy.SaveAs(savePath);
            if (!File.Exists(savePath) || new FileInfo(savePath).Length == 0)
                throw new IOException($"Checkpoint SaveAs did not produce a non-empty file at '{savePath}'.");
        }
        finally
        {
            copy.Close(false);
        }
    }

    private static void ValidateInputs(
        int maxTrials,
        int lmIterationsPerTrial,
        int initialLmIterations,
        double randomizationPercent,
        double initialMu,
        double delta,
        int maxRestarts,
        double glassSubstitutionProbability,
        int progressInterval)
    {
        if (maxTrials <= 0) throw new ArgumentOutOfRangeException(nameof(maxTrials), "maxTrials must be > 0.");
        if (lmIterationsPerTrial <= 0) throw new ArgumentOutOfRangeException(nameof(lmIterationsPerTrial), "lmIterationsPerTrial must be > 0.");
        if (initialLmIterations <= 0) throw new ArgumentOutOfRangeException(nameof(initialLmIterations), "initialLmIterations must be > 0.");
        ValidateFinite(randomizationPercent, nameof(randomizationPercent));
        if (randomizationPercent < 0 || randomizationPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(randomizationPercent), "randomizationPercent must be in 0..100.");
        ValidateFinite(initialMu, nameof(initialMu));
        if (initialMu <= 0) throw new ArgumentOutOfRangeException(nameof(initialMu), "initialMu must be > 0.");
        ValidateFinite(delta, nameof(delta));
        if (delta <= 0) throw new ArgumentOutOfRangeException(nameof(delta), "delta must be > 0.");
        if (maxRestarts < 0) throw new ArgumentOutOfRangeException(nameof(maxRestarts), "maxRestarts must be >= 0.");
        ValidateFinite(glassSubstitutionProbability, nameof(glassSubstitutionProbability));
        if (glassSubstitutionProbability < 0 || glassSubstitutionProbability > 1)
            throw new ArgumentOutOfRangeException(nameof(glassSubstitutionProbability), "glassSubstitutionProbability must be in 0..1.");
        if (progressInterval != 0)
            throw new NotSupportedException("progressInterval stderr logging was retired. Use zemax_multistart_status or MCP job progress; set progressInterval=0.");
    }

    private static void ValidateFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite.");
    }
}
