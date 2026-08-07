using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;
using ZOSAPI.Tools.RayTrace;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class RayTraceTool
{
    private readonly IZemaxSession _session;

    public RayTraceTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_ray_trace")]
    [Description("Trace one normalized unpolarized real ray through the optical system using batch ray tracing.")]
    public async Task<RayTraceResult> ExecuteAsync(
        [Description("Normalized field x coordinate (-1 to 1)")] double hx = 0,
        [Description("Normalized field y coordinate (-1 to 1)")] double hy = 0,
        [Description("Normalized pupil x coordinate (-1 to 1)")] double px = 0,
        [Description("Normalized pupil y coordinate (-1 to 1)")] double py = 0,
        [Description("Wavelength number (1-indexed)")] int wavelength = 1,
        [Description("Surface to trace to (0 for image surface)")] int surface = 0,
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

            var parameters = new Dictionary<string, object?>
            {
                ["hx"] = hx, ["hy"] = hy,
                ["px"] = px, ["py"] = py,
                ["wavelength"] = wavelength,
                ["surface"] = surface
            };

            var result = await _session.ExecuteAsync("RayTrace", parameters, system =>
            {
                var lastSurface = system.LDE.NumberOfSurfaces - 1;
                var surf = surface == 0 ? lastSurface : surface;
                if (surf < 0 || surf > lastSurface)
                    throw new ArgumentOutOfRangeException(nameof(surface), $"Surface must be 0 (image) or between 1 and {lastSurface}.");
                var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                if (wavelength > wavelengthCount)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be between 1 and {wavelengthCount}.");

                var batchRay = system.Tools.OpenBatchRayTrace();
                if (batchRay == null)
                    throw new InvalidOperationException("OpticStudio did not open Batch Ray Trace.");
                try
                {
                    var success = batchRay.SingleRayNormUnpol(
                        RaysType.Real, surf, wavelength,
                        hx, hy, px, py,
                        true,
                        out var errorCode, out var vignetteCode,
                        out var xo, out var yo, out var zo,
                        out var lo, out var mo, out var no,
                        out _, out _, out _,
                        out var opd, out _);

                    var rayValid = success && errorCode == 0;
                    return new RayTraceResult
                    {
                        Success = rayValid,
                        Error = rayValid ? null : $"Ray trace failed (error code: {errorCode}, vignette code: {vignetteCode}).",
                        X = xo,
                        Y = yo,
                        Z = zo,
                        L = lo,
                        M = mo,
                        N = no,
                        OpticalPathLength = opd,
                        SurfaceNumber = surf,
                        RayValid = rayValid
                    };
                }
                finally
                {
                    batchRay.Close();
                }
            }, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            return new RayTraceResult
            {
                Success = false,
                Error = ex.Message,
                SurfaceNumber = surface,
                RayValid = false
            };
        }
    }

    private static void ValidateNormalized(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and between -1 and 1.");
    }
}
