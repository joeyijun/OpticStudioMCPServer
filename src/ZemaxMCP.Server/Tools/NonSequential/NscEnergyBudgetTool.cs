using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.NonSequential;

/// <summary>
/// Non-sequential power accounting without pretending that adding different
/// detectors is necessarily an energy conservation statement.
/// </summary>
[ZemaxToolType]
public sealed class NscEnergyBudgetTool
{
    private readonly IZemaxSession _session;
    public NscEnergyBudgetTool(IZemaxSession session) => _session = session;

    public sealed record DetectorContribution(int ObjectNumber, string? ObjectType,
        double IncidentFlux, double RayHits, double? FractionOfLaunchedFlux);

    public sealed record Result(bool Success, string? Error,
        double? LaunchedFlux, string FluxUnit,
        IReadOnlyList<DetectorContribution> Detectors,
        string Interpretation,
        bool AdditiveEnergyBalanceValid = false);

    [ZemaxTool(Name = "zemax_nsc_energy_budget")]
    [Description("Read bounded NSC detector incident flux/hits and calculate PER-DETECTOR received/launched fractions only when an explicit launchedFlux is supplied. No trace is initiated. Never sum potentially overlapping detectors into a fictitious total efficiency.")]
    public async Task<Result> ExecuteAsync(
        [Description("Detector object numbers, 1-indexed, unique; up to 16.")] int[] detectorObjects,
        [Description("Optional launched source flux in the SAME OpticStudio power units and run as the detector readings. Omit to report absolute detector readings only.")] double? launchedFlux = null,
        CancellationToken cancellationToken = default)
    {
        var unit = "OpticStudio native NSC source-flux units";
        var caveat = "Detectors can overlap, absorb, re-emit, or record the same ray. Their fluxes must not be added to infer total system transmission. Flux ratios require a source-denominator representing the same traced sources, wavelengths, and normalization.";
        if (detectorObjects == null || detectorObjects.Length is < 1 or > 16 ||
            detectorObjects.Any(id => id <= 0) ||
            detectorObjects.Distinct().Count() != detectorObjects.Length)
            return new Result(false, "Provide 1..16 unique positive detector object numbers.", launchedFlux, unit,
                Array.Empty<DetectorContribution>(), caveat);
        if (launchedFlux.HasValue && (double.IsNaN(launchedFlux.Value) ||
            double.IsInfinity(launchedFlux.Value) || launchedFlux.Value <= 0))
            return new Result(false, "launchedFlux must be finite and positive.", launchedFlux, unit,
                Array.Empty<DetectorContribution>(), caveat);

        try
        {
            return await _session.ExecuteAsync("NscEnergyBudget",
                new Dictionary<string, object?> {
                    ["detectorObjects"] = detectorObjects,
                    ["launchedFlux"] = launchedFlux
                }, system =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (system.Mode != SystemType.NonSequential)
                        throw new InvalidOperationException("Energy budgeting requires a non-sequential system.");
                    var nce = system.NCE ?? throw new InvalidOperationException("NSC editor is unavailable.");
                    var values = new List<DetectorContribution>();
                    foreach (var id in detectorObjects)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (id > nce.NumberOfObjects)
                            throw new ArgumentOutOfRangeException(nameof(detectorObjects),
                                $"Detector object {id} exceeds the NSC object count {nce.NumberOfObjects}.");
                        var row = nce.GetObjectAt(id) ?? throw new InvalidOperationException($"NSC object {id} is missing.");
                        if (!NscObjectClassification.IsDetector(row))
                            throw new InvalidOperationException($"NSC object {id} ({row.TypeName}) is not a detector.");
                        if (row.Type is ZOSAPI.Editors.NCE.ObjectType.DetectorColor or
                            ZOSAPI.Editors.NCE.ObjectType.DetectorPolar)
                            throw new InvalidOperationException($"Color/polar detector {id} needs its dedicated detector API and cannot be used in the generic flux budget.");
                        if (!nce.GetDetectorData(id, 0, 0, out var flux) ||
                            !nce.GetDetectorData(id, -3, 0, out var hits) ||
                            double.IsNaN(flux) || double.IsInfinity(flux) ||
                            double.IsNaN(hits) || double.IsInfinity(hits))
                            throw new InvalidOperationException($"NSC detector {id} returned invalid incident flux/hits.");
                        values.Add(new DetectorContribution(id, row.TypeName, flux, hits,
                            launchedFlux.HasValue ? flux / launchedFlux.Value : null));
                    }
                    return new Result(true, null, launchedFlux, unit, values, caveat);
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, launchedFlux, unit, Array.Empty<DetectorContribution>(), caveat);
        }
    }
}
