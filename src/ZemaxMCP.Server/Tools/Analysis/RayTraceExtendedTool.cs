using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Tools.RayTrace;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class RayTraceExtendedTool
{
    private readonly IZemaxSession _session;
    public RayTraceExtendedTool(IZemaxSession session) => _session = session;

    public record Result(bool Success, string? Error = null, double X = 0, double Y = 0, double Z = 0,
        double L = 0, double M = 0, double N = 0, double OpticalPathLength = 0, double Intensity = 0,
        int ErrorCode = 0, int VignetteCode = 0, int SurfaceNumber = 0, bool RayValid = false, bool RayClear = false);

    [ZemaxTool(Name = "zemax_ray_trace_extended")]
    [Description("Trace one normalized unpolarized real ray and return intercept, direction, intensity, error and vignette codes.")]
    public async Task<Result> ExecuteAsync(
        [Description("Normalized field x coordinate (-1 to 1)")] double hx = 0,
        [Description("Normalized field y coordinate (-1 to 1)")] double hy = 0,
        [Description("Normalized pupil x coordinate (-1 to 1)")] double px = 0,
        [Description("Normalized pupil y coordinate (-1 to 1)")] double py = 0,
        [Description("Wavelength number (1-indexed)")] int wavelength = 1,
        [Description("Surface to trace to; 0 = image surface")] int surface = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateNormalized(hx, nameof(hx));
            ValidateNormalized(hy, nameof(hy));
            ValidateNormalized(px, nameof(px));
            ValidateNormalized(py, nameof(py));
            if (wavelength < 1)
                throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength number must be at least 1.");
            if (surface < 0)
                throw new ArgumentOutOfRangeException(nameof(surface), "Surface must be 0 (image) or a positive surface number.");

            return await _session.ExecuteAsync("RayTraceExtended",
                new Dictionary<string, object?>
                {
                    ["hx"] = hx, ["hy"] = hy, ["px"] = px, ["py"] = py,
                    ["wavelength"] = wavelength, ["surface"] = surface
                }, system =>
                {
                    var lastSurface = system.LDE.NumberOfSurfaces - 1;
                    var target = surface == 0 ? lastSurface : surface;
                    if (target < 0 || target > lastSurface)
                        throw new ArgumentOutOfRangeException(nameof(surface), $"Surface must be 0 (image) or between 1 and {lastSurface}.");
                    var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                    if (wavelength > wavelengthCount)
                        throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be between 1 and {wavelengthCount}.");

                    var ray = system.Tools.OpenBatchRayTrace();
                    if (ray == null)
                        throw new InvalidOperationException("OpticStudio did not open Batch Ray Trace.");
                    try
                    {
                        var apiSuccess = ray.SingleRayNormUnpol(
                            RaysType.Real, target, wavelength, hx, hy, px, py, true,
                            out var error, out var vignette,
                            out var x, out var y, out var z,
                            out var l, out var m, out var n,
                            out _, out _, out _, out var opd, out var intensity);
                        var rayValid = apiSuccess && error == 0;
                        return new Result(
                            Success: rayValid,
                            Error: rayValid ? null : $"Ray trace failed (error code: {error}, vignette code: {vignette}).",
                            X: x, Y: y, Z: z, L: l, M: m, N: n,
                            OpticalPathLength: opd, Intensity: intensity,
                            ErrorCode: error, VignetteCode: vignette, SurfaceNumber: target,
                            RayValid: rayValid,
                            RayClear: rayValid && vignette == 0);
                    }
                    finally { ray.Close(); }
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, SurfaceNumber: surface);
        }
    }

    private static void ValidateNormalized(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and between -1 and 1.");
    }
}
