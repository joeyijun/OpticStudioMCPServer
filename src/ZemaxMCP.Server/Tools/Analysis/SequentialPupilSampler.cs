using ZOSAPI.Tools.RayTrace;

namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// Shared, read-only normalized-pupil ray sampler for energy budgeting and
/// footprint diagnostics. A sample represents equal pupil-area weight, NOT
/// a calibrated radiometric source ray. Ray failures remain distinct from
/// vignetting and are never silently treated as zero-power transmissions.
/// </summary>
internal static class SequentialPupilSampler
{
    internal readonly record struct PupilPoint(double Px, double Py);
    internal readonly record struct RaySample(
        PupilPoint Pupil, bool ApiSuccess, int ErrorCode, int VignetteCode,
        double X, double Y, double Z, double Intensity)
    {
        internal bool Valid => ApiSuccess && ErrorCode == 0;
        internal bool Clear => Valid && VignetteCode == 0;
    }

    internal static PupilPoint[] CircularGrid(int gridSize)
    {
        if (gridSize is < 5 or > 101)
            throw new ArgumentOutOfRangeException(nameof(gridSize), "Pupil grid size must be 5..101.");
        var points = new List<PupilPoint>(gridSize * gridSize);
        for (int y = 0; y < gridSize; y++)
        for (int x = 0; x < gridSize; x++)
        {
            var px = -1d + 2d * x / (gridSize - 1d);
            var py = -1d + 2d * y / (gridSize - 1d);
            if (px * px + py * py <= 1d + 1e-12)
                points.Add(new PupilPoint(px, py));
        }
        return points.ToArray();
    }

    internal static RaySample[] Trace(
        IBatchRayTrace batch, int targetSurface, int wavelength,
        double hx, double hy, IReadOnlyList<PupilPoint> pupil,
        CancellationToken cancellationToken)
    {
        var rays = new RaySample[pupil.Count];
        for (int i = 0; i < pupil.Count; i++)
        {
            if ((i & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            var point = pupil[i];
            var ok = batch.SingleRayNormUnpol(RaysType.Real, targetSurface,
                wavelength, hx, hy, point.Px, point.Py, false,
                out var error, out var vignette,
                out var x, out var y, out var z,
                out _, out _, out _, out _, out _, out _, out _, out var intensity);
            if (ok && error == 0 &&
                ((double.IsNaN(intensity) || double.IsInfinity(intensity)) || intensity < 0 ||
                 (vignette == 0 && ((double.IsNaN(x) || double.IsInfinity(x)) ||
                                    (double.IsNaN(y) || double.IsInfinity(y))))))
                throw new InvalidDataException("Batch ray trace returned non-finite clear-ray coordinates or invalid intensity.");
            rays[i] = new RaySample(point, ok, error, vignette,
                ok && error == 0 ? x : 0,
                ok && error == 0 ? y : 0,
                ok && error == 0 ? z : 0,
                ok && error == 0 ? intensity : 0);
        }
        return rays;
    }

    internal static void ValidateField(double hx, double hy)
    {
        if (double.IsNaN(hx) || double.IsInfinity(hx) ||
            double.IsNaN(hy) || double.IsInfinity(hy) ||
            Math.Abs(hx) > 1 || Math.Abs(hy) > 1)
            throw new ArgumentOutOfRangeException(nameof(hx),
                "Normalized field coordinates must be finite in [-1, 1].");
    }
}
