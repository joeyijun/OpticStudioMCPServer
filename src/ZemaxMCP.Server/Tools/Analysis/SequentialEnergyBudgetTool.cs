using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// Geometric pupil survival plus ray-intensity attenuation across the
/// sequential LDE. This is a RAY-sampled energy proxy, not absolute radiant
/// power and not a coating-resolved R/T spectral measurement.
/// </summary>
[ZemaxToolType]
public sealed class SequentialEnergyBudgetTool
{
    private readonly IZemaxSession _session;
    public SequentialEnergyBudgetTool(IZemaxSession session) => _session = session;

    public sealed record SurfaceBudget(
        int Surface, string? Comment, string SurfaceType, string? Material,
        string InteractionHint, int TotalPupilSamples, int ClearRays,
        int VignettedRays, int TraceErrorRays, int VignetteCodeAtThisSurface,
        double ClearPupilFraction, double InvalidTraceFraction,
        double NormalizedClearRayIntensitySum,
        double? IntensityTransferRelativeToPriorSurface,
        double? EffectiveAttenuationRelativeToPriorSurface);
    public sealed record FieldWavelengthBudget(
        double Hx, double Hy, int WavelengthNumber, double WavelengthMicrometers,
        double? FinalClearPupilFraction, double? FinalNormalizedRayIntensity,
        int? WorstFirstVignetteSurface, IReadOnlyList<SurfaceBudget> Surfaces);
    public sealed record Result(bool Success, string? Error, int GridSize,
        int NumberOfPupilSamples, string EnergyMetric, string WavelengthUnit,
        IReadOnlyList<FieldWavelengthBudget> Cases, string Limitations);

    [ZemaxTool(Name = "zemax_energy_budget")]
    [Description("Calculate a BOUNDED sequential field/wavelength/surface energy budget for a consecutive LDE window using the same circular normalized pupil ray sampling as aperture throughput. Reports geometric clipping, ray failures and unpolarized ray-intensity attenuation per surface. Does NOT equate ray intensity to absolute watts, or invent separate coating reflectance/absorption/transmission or NSC detector efficiency.")]
    public async Task<Result> ExecuteAsync(
        [Description("Normalized field coordinates [[hx,hy],...] with up to 6 samples; omitted means on-axis [0,0].")] double[][]? fields = null,
        [Description("1-based wavelength indices; 0/empty means the configured primary wavelength. Up to 6 unique indices.")] int[]? wavelengths = null,
        [Description("Last sequential LDE surface to include, 0 means image plane. At most 24 consecutive surfaces per window.")] int finalSurface = 0,
        [Description("Circular normalized pupil grid dimension from 5 to 41.")] int gridSize = 15,
        [Description("First LDE surface in a <=24-surface window. For long LDEs use overlapping windows (1..24, 24..47).")] int startSurface = 1,
        CancellationToken cancellationToken = default)
    {
        const string limits = "The first surface in a selected window has no preceding-surface transfer. For long LDEs use overlapping windows (1..24, 24..47). Each surface still reports full-entrance pupil survival; do not multiply cumulative fractions from separate windows. Geometric survival excludes vignetted rays but reports trace errors separately. Equal pupil-area ray sampling is NOT source radiance, Watts, spectral-weighted efficiency or an NSC detector measurement. Ray intensity follows the OpticStudio unpolarized real-ray engine (coatings/material path); reflection, transmission, scattering and absorption are not separately identifiable from this scalar intensity. Material=MIRROR is merely a surface interaction hint. A real NSC detector requires a separate trace and launched-flux denominator.";
        try
        {
            var selectedFields = fields is { Length: > 0 } ? fields : new[] { new[] { 0d, 0d } };
            if (selectedFields.Length is < 1 or > 6 ||
                selectedFields.Any(x => x == null || x.Length != 2))
                throw new ArgumentException("fields must contain 1..6 two-element normalized [hx,hy] pairs.");
            foreach (var v in selectedFields) SequentialPupilSampler.ValidateField(v[0], v[1]);
            if (gridSize is < 5 or > 41)
                throw new ArgumentException("gridSize must be 5..41.");

            return await _session.ExecuteAsync("SequentialEnergyBudget",
                new Dictionary<string, object?> {
                    ["fields"] = selectedFields, ["wavelengths"] = wavelengths,
                    ["finalSurface"] = finalSurface, ["startSurface"] = startSurface, ["gridSize"] = gridSize
                }, system =>
                {
                    if (system.Mode != SystemType.Sequential)
                        throw new InvalidOperationException("zemax_energy_budget currently requires a sequential lens; use zemax_nsc_energy_budget for NSC detector readings.");

                    var lastLde = system.LDE.NumberOfSurfaces - 1;
                    var (first, last, surfaceCount) = SequentialEnergySurfaceRange.Resolve(startSurface, finalSurface, lastLde);

                    var waveData = system.SystemData.Wavelengths;
                    var primary = Enumerable.Range(1, waveData.NumberOfWavelengths)
                        .FirstOrDefault(i => waveData.GetWavelength(i).IsPrimary);
                    if (primary == 0) primary = 1;
                    var selectedWaves = wavelengths is { Length: > 0 } ? wavelengths : new[] { primary };
                    if (selectedWaves.Length is < 1 or > 6 ||
                        selectedWaves.Distinct().Count() != selectedWaves.Length ||
                        selectedWaves.Any(x => x < 1 || x > waveData.NumberOfWavelengths))
                        throw new ArgumentException("Choose 1..6 unique existing wavelength numbers.");

                    var pupil = SequentialPupilSampler.CircularGrid(gridSize);
                    if ((long)pupil.Length * surfaceCount * selectedFields.Length * selectedWaves.Length > 150000)
                        throw new ArgumentException("Requested energy budget exceeds 150000 ray/surface samples. Reduce fields, wavelengths, surfaces or grid size.");

                    var output = new List<FieldWavelengthBudget>();
                    var batch = system.Tools.OpenBatchRayTrace()
                        ?? throw new InvalidOperationException("Batch ray tracing is unavailable.");
                    try
                    {
                        foreach (var field in selectedFields)
                        foreach (var wave in selectedWaves)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var surfaces = new List<SurfaceBudget>();
                            SequentialPupilSampler.RaySample[]? previous = null;
                            for (var surface = first; surface <= last; surface++)
                            {
                                var samples = SequentialPupilSampler.Trace(batch, surface, wave,
                                    field[0], field[1], pupil, cancellationToken);
                                var clear = samples.Where(x => x.Clear).ToArray();
                                var valid = samples.Count(x => x.Valid);
                                var totalIntensity = clear.Sum(x => x.Intensity);
                                double? transfer = null, loss = null;
                                if (previous != null)
                                {
                                    double incoming = 0, outgoing = 0;
                                    for (var i = 0; i < samples.Length; i++)
                                    {
                                        if (!samples[i].Clear || !previous[i].Clear) continue;
                                        incoming += previous[i].Intensity;
                                        outgoing += samples[i].Intensity;
                                    }
                                    if (incoming > 0)
                                    {
                                        transfer = outgoing / incoming;
                                        if (double.IsNaN(transfer.Value) || double.IsInfinity(transfer.Value))
                                            throw new InvalidDataException("Non-finite ray-intensity surface transmission.");
                                        if (transfer.Value >= 0 && transfer.Value <= 1)
                                            loss = 1 - transfer.Value;
                                    }
                                }
                                var row = system.LDE.GetSurfaceAt(surface);
                                var mirror = string.Equals(row.Material?.Trim(), "MIRROR", StringComparison.OrdinalIgnoreCase);
                                surfaces.Add(new SurfaceBudget(surface, row.Comment, row.Type.ToString(), row.Material,
                                    mirror ? "mirror-path; effective reflection and coating loss inseparable" :
                                        "refracting/other-path; transmission and absorption inseparable",
                                    pupil.Length, clear.Length, valid - clear.Length, pupil.Length - valid,
                                    samples.Count(x => x.Valid && x.VignetteCode == surface),
                                    (double)clear.Length / pupil.Length,
                                    (double)(pupil.Length - valid) / pupil.Length,
                                    totalIntensity / pupil.Length, transfer, loss));
                                previous = samples;
                            }
                            var final = surfaces[surfaces.Count - 1];
                            var worst = surfaces.OrderByDescending(x => x.VignetteCodeAtThisSurface)
                                .FirstOrDefault(x => x.VignetteCodeAtThisSurface > 0);
                            output.Add(new FieldWavelengthBudget(field[0], field[1], wave,
                                waveData.GetWavelength(wave).Wavelength,
                                final.ClearPupilFraction, final.NormalizedClearRayIntensitySum,
                                worst?.Surface, surfaces));
                        }
                    }
                    finally { batch.Close(); }

                    return new Result(true, null, gridSize, pupil.Length,
                        "equal-area normalized-pupil ray intensity proxy (dimensionless)",
                        "micrometers", output, limits);
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, gridSize, 0,
                "equal-area normalized-pupil ray intensity proxy (dimensionless)",
                "micrometers", Array.Empty<FieldWavelengthBudget>(), limits);
        }
    }
}
