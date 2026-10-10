using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// Read-only engineering diagnosis, combining two existing, independently
/// exposed ray samplers. No optical change, detector reset or geometry
/// interpretation beyond OpticStudio's reported vignette codes.
/// </summary>
[ZemaxToolType]
public sealed class DiagnoseClippingTool
{
    private readonly RayFootprintTool _footprints;
    private readonly ApertureThroughputTool _aperture;

    public DiagnoseClippingTool(IZemaxSession session)
    {
        _footprints = new RayFootprintTool(session);
        _aperture = new ApertureThroughputTool(session);
    }

    public sealed record Suspect(int Surface, string? Comment, int VignettedRayCount,
        double FractionOfSuccessfullyTracedPupil,
        string Evidence, string? PhysicalApertureType,
        double? MinimumClearRayApertureClearance);

    public sealed record Result(
        bool Success, string? Error, int Wavelength, double Hx, double Hy,
        int GridSize, int DestinationSurface, int TotalSampledRays,
        int SuccessfullyTracedRays, int RayTraceErrors, int ClearRays,
        int VignettedRays, double? GeometricClearFraction,
        int? MostLikelyBlockingSurface, IReadOnlyList<Suspect> Suspects,
        IReadOnlyList<RayFootprintTool.SurfaceFootprint> Footprints,
        IReadOnlyList<string> Warnings, string Interpretation);

    [ZemaxTool(Name = "zemax_diagnose_clipping")]
    [Description("Answer 'where and why are rays clipped?' for a sequential lens by combining local ray footprints and independently cross-checked pupil throughput. Reports vignette-code ranked suspect surfaces, trace errors, explicit aperture geometry, and actionable caveats. Read-only; does NOT assume lens semi-diameter is a mechanical stop.")]
    public async Task<Result> ExecuteAsync(
        [Description("Surfaces to inspect, max 24; omitted means all surfaces only when the LDE has <=24 target surfaces. Include the destination surface.")] int[]? surfaces = null,
        [Description("Normalized field coordinate X [-1,1].")] double hx = 0,
        [Description("Normalized field coordinate Y [-1,1].")] double hy = 0,
        [Description("Configured 1-based wavelength index.")] int wavelength = 1,
        [Description("Circular pupil-grid dimension 5..41.")] int gridSize = 21,
        [Description("Maximum returned clear-ray points per surface, 0..128.")] int maxPointsPerSurface = 0,
        CancellationToken cancellationToken = default)
    {
        const string caution = "Vignetting is ranked by the trace engine's reported blocking surface, not by mechanical CAD intersection. Footprint X/Y and aperture geometry use each LDE surface's local coordinate frame. A semi-diameter alone is not a declared physical aperture. Ray-trace errors are excluded from the geometric clear-fraction denominator; unmodeled mounts and stops cannot be diagnosed.";
        Result Failure(string error) => new(false, error, wavelength, hx, hy, gridSize,
            0, 0, 0, 0, 0, 0, null, null, Array.Empty<Suspect>(),
            Array.Empty<RayFootprintTool.SurfaceFootprint>(), new[] { error }, caution);
        try
        {
            if (gridSize is < 5 or > 41 ||
                maxPointsPerSurface is < 0 or > 128)
                return Failure("gridSize must be 5..41 and maxPointsPerSurface 0..128.");
            cancellationToken.ThrowIfCancellationRequested();
            var fp = await _footprints.ExecuteAsync(surfaces, hx, hy, wavelength,
                gridSize, maxPointsPerSurface, cancellationToken).ConfigureAwait(false);
            if (!fp.Success || fp.Surfaces.Count == 0)
                return Failure(fp.Error ?? "No readable sequential ray footprints.");
            var destination = fp.Surfaces.Max(x => x.Surface);
            // Identical sampling grid and destination: these two tools should
            // return identical counts, otherwise fail instead of publishing an
            // internally contradictory clipping diagnosis.
            var aperture = await _aperture.ExecuteAsync(hx, hy, wavelength,
                destination, gridSize, cancellationToken).ConfigureAwait(false);
            if (!aperture.Success)
                return Failure(aperture.Error ?? "Pupil throughput cross-check failed.");
            var final = fp.Surfaces.Single(x => x.Surface == destination);
            if (aperture.TotalPupilRays != final.PupilRays ||
                aperture.ClearRays != final.ClearRays ||
                aperture.VignettedRays != final.VignettedRays ||
                aperture.ErrorRays != final.TraceErrorRays)
                return Failure("Ray Footprint and Aperture Throughput disagree on the same pupil sample/destination. No clipping conclusion was returned.");

            var known = fp.Surfaces.ToDictionary(x => x.Surface);
            var warnings = new List<string>();
            if (aperture.ErrorRays > 0)
                warnings.Add("Some rays failed to trace; do not classify them as geometric clipping.");
            var suspects = (aperture.VignetteBySurface ?? Array.Empty<ApertureThroughputTool.VignetteCount>())
                .OrderByDescending(x => x.Count).ThenBy(x => x.Surface)
                .Take(8)
                .Select(x =>
                {
                    known.TryGetValue(x.Surface, out var row);
                    var fraction = aperture.SuccessfulRays > 0
                        ? (double)x.Count / aperture.SuccessfulRays : 0;
                    return new Suspect(x.Surface, row?.Comment, x.Count, fraction,
                        row == null ? "Reported vignette code; surface not in selected footprint set" :
                            "Reported vignette code; local footprint available",
                        row?.ApertureType, row?.MinimumOuterApertureClearance);
                }).ToArray();
            var mostLikely = suspects.Length > 0 ? suspects[0].Surface : (int?)null;
            if (suspects.Any(x => !known.ContainsKey(x.Surface)))
                warnings.Add("One or more blocking surfaces were not sampled. Add those LDE numbers to surfaces for local footprint and aperture information.");
            if (suspects.Any(x => x.PhysicalApertureType == "None" || x.PhysicalApertureType == null))
                warnings.Add("A vignette surface may have no explicit circular aperture; inspect other native apertures, stops, edge/coordinate settings and unmodeled mechanics.");
            if (aperture.VignettedRays == 0)
                warnings.Add("No modeled geometric clipping was identified for this field/wavelength/pupil sample. Mechanical CAD not present in the lens remains untested.");
            if (aperture.SuccessfulRays <= 0)
                return Failure("No successfully traced rays; clipping fraction is undefined.");
            return new Result(true, null, wavelength, hx, hy, gridSize,
                destination, aperture.TotalPupilRays, aperture.SuccessfulRays,
                aperture.ErrorRays, aperture.ClearRays, aperture.VignettedRays,
                aperture.ClearFraction, mostLikely, suspects, fp.Surfaces,
                warnings, caution);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure(ex.Message); }
    }
}
