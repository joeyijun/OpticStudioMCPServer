using ZemaxMCP.Core.Models;
using ZOSAPI;

namespace ZemaxMCP.Core.Services.ConstrainedOptimization;

public class MultistartOptimizer
{
    private readonly MeritFunctionReader _meritReader;

    public MultistartOptimizer(MeritFunctionReader meritReader)
    {
        _meritReader = meritReader ?? throw new ArgumentNullException(nameof(meritReader));
    }

    public MultistartResult Optimize(
        IOpticalSystem system,
        List<OptVariable> variables,
        List<MaterialInfo> substituteMaterials,
        int maxTrials = 100,
        int lmIterationsPerTrial = 50,
        int initialLmIterations = 200,
        double randomizationPercent = 5.0,
        double initialMu = 1e-3,
        double delta = 1e-7,
        bool useBroydenUpdate = true,
        int maxRestarts = 0,
        bool constrainedOnly = false,
        double glassSubstitutionProbability = 0.5,
        int progressInterval = 0,
        bool skipInitialLm = false,
        Action<int, double>? onImprovement = null,
        Action<int, int, double, int>? onProgress = null,
        Action? onInitialLmComplete = null,
        Action<int, int, double>? onInitialLmProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (variables == null) throw new ArgumentNullException(nameof(variables));
        if (substituteMaterials == null) throw new ArgumentNullException(nameof(substituteMaterials));
        ValidateInputs(maxTrials, lmIterationsPerTrial, initialLmIterations, randomizationPercent,
            initialMu, delta, maxRestarts, glassSubstitutionProbability, progressInterval);

        var result = new MultistartResult();
        var rng = new Random();
        DesignState? bestState = null;
        int trialsAccepted = 0;
        int trialsRun = 0;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            double initialMerit = system.MFE.CalculateMeritFunction();
            ValidateFinite(initialMerit, "initial merit function");
            result.InitialMerit = initialMerit;

            var lmOptimizer = new LMOptimizer(_meritReader);
            double postLmMerit;
            if (skipInitialLm)
            {
                postLmMerit = initialMerit;
            }
            else
            {
                var lmResult = lmOptimizer.Optimize(
                    system,
                    variables,
                    initialLmIterations,
                    initialMu,
                    delta,
                    useBroydenUpdate: useBroydenUpdate,
                    maxRestarts: maxRestarts,
                    onIterationProgress: onInitialLmProgress,
                    cancellationToken: cancellationToken);
                if (!lmResult.Success)
                    throw new InvalidOperationException($"Initial LM optimization failed: {lmResult.Message}");
                postLmMerit = lmResult.FinalMerit;
                ValidateFinite(postLmMerit, "post-initial-LM merit function");
            }

            result.PostInitialLmMerit = postLmMerit;
            cancellationToken.ThrowIfCancellationRequested();
            onInitialLmComplete?.Invoke();
            bestState = CaptureState(system, variables, substituteMaterials, postLmMerit, cancellationToken);

            double fraction = randomizationPercent / 100.0;
            for (int trial = 1; trial <= maxTrials; trial++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                trialsRun = trial;
                RandomizeVariables(system, variables, bestState.VariableValues, fraction, rng, constrainedOnly, cancellationToken);

                if (substituteMaterials.Count > 0 && rng.NextDouble() < glassSubstitutionProbability)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RandomizeOneGlass(system, substituteMaterials, rng);
                }

                var trialResult = lmOptimizer.Optimize(
                    system,
                    variables,
                    lmIterationsPerTrial,
                    initialMu,
                    delta,
                    useBroydenUpdate: useBroydenUpdate,
                    maxRestarts: 0,
                    cancellationToken: cancellationToken);
                if (!trialResult.Success)
                    throw new InvalidOperationException($"LM optimization failed in multistart trial {trial}: {trialResult.Message}");
                ValidateFinite(trialResult.FinalMerit, $"trial {trial} merit function");

                if (trialResult.FinalMerit < bestState.Merit)
                {
                    bestState = CaptureState(system, variables, substituteMaterials, trialResult.FinalMerit, cancellationToken);
                    trialsAccepted++;
                    onImprovement?.Invoke(trial, trialResult.FinalMerit);
                }
                else
                {
                    RestoreState(system, variables, substituteMaterials, bestState);
                    system.MFE.CalculateMeritFunction();
                }

                cancellationToken.ThrowIfCancellationRequested();
                result.TrialsRun = trial;
                result.TrialsAccepted = trialsAccepted;
                result.FinalMerit = bestState.Merit;
                onProgress?.Invoke(trial, maxTrials, bestState.Merit, trialsAccepted);
                if (progressInterval > 0 && trial % progressInterval == 0)
                {
                    // Kept for backward compatibility with callers that use the interval
                    // to control callback cadence; stderr side-channel output is retired.
                }
            }

            RestoreState(system, variables, substituteMaterials, bestState);
            system.MFE.CalculateMeritFunction();
            result.TrialsRun = maxTrials;
            result.TrialsAccepted = trialsAccepted;
            result.FinalMerit = bestState.Merit;
            result.SubstituteMaterialsFound = substituteMaterials.Count;
            result.Success = true;
            var glassInfo = substituteMaterials.Count > 0
                ? $" Glass substitution active on {substituteMaterials.Count} surface(s)."
                : " No MaterialSubstitute surfaces found.";
            result.Message = $"Multistart optimization completed. {trialsAccepted}/{maxTrials} trials accepted.{glassInfo} " +
                             $"Merit: {result.InitialMerit:F6} -> {result.PostInitialLmMerit:F6} (initial LM) -> {result.FinalMerit:F6} (multistart)";
            return result;
        }
        catch (OperationCanceledException)
        {
            if (bestState != null) RestoreBestState(system, variables, substituteMaterials, bestState);
            result.TrialsRun = trialsRun;
            result.TrialsAccepted = trialsAccepted;
            if (bestState != null) result.FinalMerit = bestState.Merit;
            throw;
        }
        catch (Exception ex)
        {
            if (bestState != null) RestoreBestState(system, variables, substituteMaterials, bestState);
            result.TrialsRun = trialsRun;
            result.TrialsAccepted = trialsAccepted;
            if (bestState != null) result.FinalMerit = bestState.Merit;
            result.Success = false;
            result.Message = $"Multistart optimization failed after trial {trialsRun}: {ex.Message}";
            return result;
        }
    }

    private static void RandomizeVariables(
        IOpticalSystem system,
        List<OptVariable> variables,
        double[] bestValues,
        double fraction,
        Random rng,
        bool constrainedOnly,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < variables.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var variable = variables[i];
            double bestValue = bestValues[i];
            double range;
            if (variable.Constraint == ConstraintType.Unconstrained)
            {
                if (constrainedOnly) continue;
                range = Math.Abs(bestValue) * fraction;
                if (range < 1e-10) range = fraction;
            }
            else
            {
                range = (variable.UpperBound - variable.LowerBound) * fraction;
            }

            double offset = (rng.NextDouble() * 2.0 - 1.0) * range;
            double newValue = Math.Max(variable.LowerBound, Math.Min(variable.UpperBound, bestValue + offset));
            ZosVariableAccessor.SetVariableValue(system, variable, newValue);
            variable.Value = newValue;
        }
    }

    private static void RandomizeOneGlass(IOpticalSystem system, List<MaterialInfo> materials, Random rng)
    {
        var eligible = materials.Where(material =>
            material.SolveType == ZOSAPI.Editors.SolveType.MaterialSubstitute &&
            material.SubstituteGlasses != null && material.SubstituteGlasses.Length > 1).ToList();
        if (eligible.Count == 0) return;

        var chosen = eligible[rng.Next(eligible.Count)];
        string currentGlass = ZosVariableAccessor.GetGlassMaterial(system, chosen.SurfaceIndex);
        string? newGlass = PickRandomDifferentGlass(rng, chosen.SubstituteGlasses, currentGlass);
        if (newGlass != null) ZosVariableAccessor.SetGlassMaterial(system, chosen.SurfaceIndex, newGlass);
    }

    private static string? PickRandomDifferentGlass(Random rng, string[] glasses, string currentGlass)
    {
        if (glasses.Length <= 1) return null;
        for (int i = 0; i < 10; i++)
        {
            string candidate = glasses[rng.Next(glasses.Length)];
            if (!string.Equals(candidate, currentGlass, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return glasses.FirstOrDefault(glass => !string.Equals(glass, currentGlass, StringComparison.OrdinalIgnoreCase));
    }

    private static DesignState CaptureState(
        IOpticalSystem system,
        List<OptVariable> variables,
        List<MaterialInfo> substituteMaterials,
        double merit,
        CancellationToken cancellationToken)
    {
        var state = new DesignState
        {
            Merit = merit,
            VariableValues = new double[variables.Count],
            GlassAssignments = new string[substituteMaterials.Count]
        };
        for (int i = 0; i < variables.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.VariableValues[i] = ZosVariableAccessor.GetVariableValue(system, variables[i]);
        }
        for (int i = 0; i < substituteMaterials.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.GlassAssignments[i] = ZosVariableAccessor.GetGlassMaterial(system, substituteMaterials[i].SurfaceIndex);
        }
        return state;
    }

    private static void RestoreState(IOpticalSystem system, List<OptVariable> variables,
        List<MaterialInfo> substituteMaterials, DesignState state)
    {
        for (int i = 0; i < substituteMaterials.Count; i++)
            ZosVariableAccessor.SetGlassMaterial(system, substituteMaterials[i].SurfaceIndex, state.GlassAssignments[i]);
        for (int i = 0; i < variables.Count; i++)
        {
            ZosVariableAccessor.SetVariableValue(system, variables[i], state.VariableValues[i]);
            variables[i].Value = state.VariableValues[i];
        }
    }

    private static void RestoreBestState(IOpticalSystem system, List<OptVariable> variables,
        List<MaterialInfo> substituteMaterials, DesignState state)
    {
        try
        {
            RestoreState(system, variables, substituteMaterials, state);
            system.MFE.CalculateMeritFunction();
        }
        catch { }
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
        if (progressInterval < 0) throw new ArgumentOutOfRangeException(nameof(progressInterval), "progressInterval must be >= 0.");
    }

    private static void ValidateFinite(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException($"Multistart optimization encountered a non-finite {label}.");
    }

    private sealed class DesignState
    {
        public double[] VariableValues { get; set; } = Array.Empty<double>();
        public string[] GlassAssignments { get; set; } = Array.Empty<string>();
        public double Merit { get; set; }
    }
}
