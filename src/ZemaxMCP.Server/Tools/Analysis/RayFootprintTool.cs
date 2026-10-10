using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;
using ZOSAPI.Editors.LDE;

namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Per-surface local footprints and optional GetGlobalMatrix-based true 3D ray intercepts.</summary>
[ZemaxToolType]
public sealed class RayFootprintTool
{
    private readonly IZemaxSession _session;
    public RayFootprintTool(IZemaxSession session) => _session = session;

    public sealed record FootprintPoint(double Px, double Py, double X, double Y, double Intensity);
    public sealed record SurfaceFootprint(
        int Surface, string? Comment, string SurfaceType, string? ApertureType,
        double? ReferenceSemiDiameter, double? CircularMinimumRadius,
        double? CircularMaximumRadius, double? CircularXDecenter,
        double? CircularYDecenter, int PupilRays, int ClearRays,
        int VignettedRays, int TraceErrorRays, int? FirstVignettingCodeRays,
        double? CentroidX, double? CentroidY, double? MinX, double? MaxX,
        double? MinY, double? MaxY, double? RmsRadius,
        double? MinimumOuterApertureClearance,
        IReadOnlyList<FootprintPoint> Points, bool PointsTruncated,
        MechanicalFootprintBoundary.Assessment? UserMechanicalBoundary = null,
        GlobalFootprintProjection.Result? GlobalCoordinates = null);
    public sealed record Result(bool Success, string? Error,
        int Wavelength, double Hx, double Hy, int GridSize, string PositionUnit,
        IReadOnlyList<SurfaceFootprint> Surfaces,
        int? MostCriticalVignettingSurface, string Interpretation);

    [ZemaxTool(Name = "zemax_ray_footprint")]
    [Description("Trace a bounded real-ray pupil grid to selected sequential LDE surfaces. Return per-surface LOCAL X/Y footprint envelope, centroid/RMS, effective explicit circular aperture boundary, first-vignette signatures, clipping and trace-error counts; optionally sample points for plotting. Optical semi-diameter is informational, not a guaranteed clear aperture.")]
    public async Task<Result> ExecuteAsync(
        [Description("Sequential LDE target surface numbers (1-indexed), unique, at most 24. Empty selects all physical surfaces up to image when <=24.")] int[]? surfaces = null,
        [Description("Normalized field X in [-1,1].")] double hx = 0,
        [Description("Normalized field Y in [-1,1].")] double hy = 0,
        [Description("1-based wavelength number.")] int wavelength = 1,
        [Description("Circular normalized-pupil grid dimension, 5..51.")] int gridSize = 21,
        [Description("Include up to 128 representative clear ray intercepts per surface; 0 returns envelopes only.")] int maxPointsPerSurface = 0,
        [Description("Optional real mechanical rectangle [minX,minY,maxX,maxY] in selected surface LOCAL lens units.")] double[]? mechanicalRectangle = null,
        [Description("Optional mechanical polygon [[x,y],...], 3..64 vertices in selected surface LOCAL lens units.")] double[][]? mechanicalPolygon = null,
        [Description("Surface with supplied mechanical boundary; 0 only if one target surface is selected.")] int mechanicalSurface = 0,
        [Description("Also return real 3D global-coordinate envelope/centroid and up to maxPointsPerSurface transformed clear rays. Uses actual traced local Z (surface sag) and official LDE GetGlobalMatrix; not a tangent-plane approximation.")] bool includeGlobalCoordinates = false,
        CancellationToken cancellationToken = default)
    {
        const string note = "User mechanical outlines are assessed only against surviving clear rays, not entrance-pupil throughput. User vertices and intercepts must share the same target LOCAL coordinate frame; this does not edit apertures or transform global CAD. Optional global intercepts use traced local Z and OpticStudio's LDE GetGlobalMatrix. Local LDE coordinates vary across coordinate breaks and folded systems. Explicit circular apertures are geometry; semi-diameter alone does not cut rays. VignetteCode identifies the reported blocking surface; ray-trace errors are not geometric clipping. Clear-ray centroid/RMS exclude blocked and invalid rays.";
        var empty = Array.Empty<SurfaceFootprint>();
        try
        {
            SequentialPupilSampler.ValidateField(hx, hy);
            if (mechanicalRectangle != null && mechanicalPolygon != null)
                throw new ArgumentException("Choose mechanicalRectangle OR mechanicalPolygon.");
            var mechanical = mechanicalRectangle != null ?
                MechanicalFootprintBoundary.Rectangle(mechanicalRectangle) : mechanicalPolygon;
            if (mechanical != null) MechanicalFootprintBoundary.Validate(mechanical);
            if (mechanical == null && mechanicalSurface != 0)
                throw new ArgumentException("mechanicalSurface requires an outline.");
            if (wavelength < 1 || gridSize is < 5 or > 51 ||
                maxPointsPerSurface is < 0 or > 128 ||
                (surfaces != null && (surfaces.Length > 24 || surfaces.Distinct().Count() != surfaces.Length)))
                throw new ArgumentException("Invalid wavelength, surface list, gridSize (5..51), or maxPointsPerSurface (0..128).");

            return await _session.ExecuteAsync("RayFootprint",
                new Dictionary<string, object?> {
                    ["surfaces"] = surfaces, ["hx"] = hx, ["hy"] = hy, ["wavelength"] = wavelength,
                    ["gridSize"] = gridSize, ["maxPointsPerSurface"] = maxPointsPerSurface,
                    ["mechanicalSurface"] = mechanicalSurface,
                    ["mechanicalRectangle"] = mechanicalRectangle, ["mechanicalPolygon"] = mechanicalPolygon,
                    ["includeGlobalCoordinates"] = includeGlobalCoordinates
                }, system =>
                {
                    if (system.Mode != SystemType.Sequential)
                        throw new InvalidOperationException("Ray footprint requires sequential mode.");
                    var last = system.LDE.NumberOfSurfaces - 1;
                    if (wavelength > system.SystemData.Wavelengths.NumberOfWavelengths)
                        throw new ArgumentOutOfRangeException(nameof(wavelength));
                    var targets = surfaces is { Length: > 0 }
                        ? surfaces
                        : Enumerable.Range(1, Math.Max(0, last)).ToArray();
                    if (targets.Length is < 1 or > 24 ||
                        targets.Any(x => x < 1 || x > last))
                        throw new ArgumentException("Choose between 1 and 24 valid LDE surface numbers.");
                    var comparisonSurface = mechanicalSurface == 0 && targets.Length == 1 ?
                        targets[0] : mechanicalSurface;
                    if (mechanical != null &&
                        (comparisonSurface < 1 || !targets.Contains(comparisonSurface)))
                        throw new ArgumentException("mechanicalSurface must match an inspected LDE target surface.");
                    var pupil = SequentialPupilSampler.CircularGrid(gridSize);
                    if ((long)pupil.Length * targets.Length > 45000)
                        throw new ArgumentException("Ray sampling request exceeds 45000 bounded ray/surface traces.");
                    var trace = system.Tools.OpenBatchRayTrace()
                        ?? throw new InvalidOperationException("Batch ray trace is unavailable.");
                    var output = new List<SurfaceFootprint>();
                    try
                    {
                        foreach (var target in targets)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var row = system.LDE.GetSurfaceAt(target);
                            var rays = SequentialPupilSampler.Trace(
                                trace, target, wavelength, hx, hy, pupil, cancellationToken);
                            var clear = rays.Where(x => x.Clear).ToArray();
                            var valid = rays.Where(x => x.Valid).ToArray();
                            var blocked = valid.Length - clear.Length;
                            double? centerX = null, centerY = null, minX = null, maxX = null, minY = null, maxY = null, rms = null;
                            if (clear.Length > 0)
                            {
                                centerX = clear.Average(x => x.X);
                                centerY = clear.Average(x => x.Y);
                                minX = clear.Min(x => x.X);
                                maxX = clear.Max(x => x.X);
                                minY = clear.Min(x => x.Y);
                                maxY = clear.Max(x => x.Y);
                                rms = Math.Sqrt(clear.Average(x => Math.Pow(x.X - centerX.Value, 2) +
                                    Math.Pow(x.Y - centerY.Value, 2)));
                            }
                            var apertureType = row.ApertureData.CurrentType;
                            double? inner = null, outer = null, dx = null, dy = null, clearance = null;
                            if (apertureType is SurfaceApertureTypes.CircularAperture or SurfaceApertureTypes.CircularObscuration)
                            {
                                var settings = row.ApertureData.CurrentTypeSettings;
                                ISurfaceApertureCircular circle = apertureType == SurfaceApertureTypes.CircularAperture
                                    ? settings._S_CircularAperture
                                    : settings._S_CircularObscuration;
                                inner = circle.MinimumRadius;
                                outer = circle.MaximumRadius;
                                dx = circle.ApertureXDecenter;
                                dy = circle.ApertureYDecenter;
                                if (clear.Length > 0 && apertureType == SurfaceApertureTypes.CircularAperture)
                                    clearance = clear.Min(x => outer.Value -
                                        Math.Sqrt(Math.Pow(x.X - dx.Value, 2) + Math.Pow(x.Y - dy.Value, 2)));
                            }
                            var semi = row.SemiDiameter;
                            double? reference = !double.IsNaN(semi) && !double.IsInfinity(semi) && semi >= 0 ? semi : null;
                            var points = clear.Take(maxPointsPerSurface)
                                .Select(x => new FootprintPoint(x.Pupil.Px, x.Pupil.Py, x.X, x.Y, x.Intensity))
                                .ToArray();
                            var mechanicalResult = mechanical != null && target == comparisonSurface
                                ? MechanicalFootprintBoundary.Assess(
                                    mechanical, clear.Select(ray => (ray.X,ray.Y)).ToArray(),
                                    mechanicalRectangle != null ? "user-rectangle" : "user-polygon")
                                : null;
                            GlobalFootprintProjection.Result? global = null;
                            if (includeGlobalCoordinates)
                            {
                                if (!system.LDE.GetGlobalMatrix(target,
                                    out var r11, out var r12, out var r13,
                                    out var r21, out var r22, out var r23,
                                    out var r31, out var r32, out var r33,
                                    out var originX, out var originY, out var originZ))
                                    throw new InvalidOperationException("Official LDE GetGlobalMatrix failed for surface " + target + ".");
                                global = GlobalFootprintProjection.Project(
                                    new[] { new[] { r11,r12,r13 }, new[] { r21,r22,r23 }, new[] { r31,r32,r33 } },
                                    new[] { originX,originY,originZ },
                                    clear.Select(ray => (ray.X,ray.Y,ray.Z)).ToArray(),
                                    maxPointsPerSurface);
                            }
                            output.Add(new SurfaceFootprint(target, row.Comment, row.Type.ToString(),
                                apertureType.ToString(), reference, inner, outer, dx, dy, pupil.Length,
                                clear.Length, blocked, rays.Length - valid.Length,
                                rays.Count(x => x.Valid && x.VignetteCode == target),
                                centerX, centerY, minX, maxX, minY, maxY, rms, clearance,
                                points, clear.Length > points.Length, mechanicalResult, global));
                        }
                    }
                    finally { trace.Close(); }

                    var worst = output.OrderByDescending(x => x.FirstVignettingCodeRays ?? 0)
                        .FirstOrDefault(x => x.FirstVignettingCodeRays > 0);
                    return new Result(true, null, wavelength, hx, hy, gridSize, "lens units",
                        output, worst?.Surface, note);
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, wavelength, hx, hy, gridSize,
                "lens units", empty, null, note);
        }
    }
}
