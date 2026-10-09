using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Tools.RayTrace;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class ApertureThroughputTool
{
    private readonly IZemaxSession _session;

    public ApertureThroughputTool(IZemaxSession session) => _session = session;

    public record VignetteCount(int Surface, int Count);

    public record Result(
        bool Success,
        string? Error = null,
        int SurfaceNumber = 0,
        int GridSize = 0,
        int TotalPupilRays = 0,
        int SuccessfulRays = 0,
        int ClearRays = 0,
        int VignettedRays = 0,
        int ErrorRays = 0,
        double ClearFraction = 0,
        double IntensityWeightedFraction = 0,
        VignetteCount[]? VignetteBySurface = null);

    [ZemaxTool(Name = "zemax_aperture_throughput")]
    [Description("Trace a circular normalized-pupil grid for one normalized field point and report real aperture/obscuration throughput, trace errors, and vignette surface counts. ClearFraction excludes ray-trace errors from the aperture-throughput denominator.")]
    public async Task<Result> ExecuteAsync(
        [Description("Normalized field X coordinate in [-1, 1].")] double hx = 0,
        [Description("Normalized field Y coordinate in [-1, 1].")] double hy = 0,
        [Description("Wavelength number, 1-indexed.")] int wavelength = 1,
        [Description("Destination surface. 0 means the image surface; otherwise use a positive sequential surface number.")] int surface = 0,
        [Description("Square pupil-grid dimension before circular clipping; 5..101.")] int gridSize = 41,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateNormalized(nameof(hx), hx);
            ValidateNormalized(nameof(hy), hy);
            if (wavelength <= 0) throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be >= 1.");
            if (surface < 0) throw new ArgumentOutOfRangeException(nameof(surface), "Surface must be >= 0; 0 means image surface.");
            if (gridSize < 5 || gridSize > 101) throw new ArgumentOutOfRangeException(nameof(gridSize), "gridSize must be between 5 and 101.");

            return await _session.ExecuteAsync("ApertureThroughput", new Dictionary<string, object?>
            {
                ["hx"] = hx,
                ["hy"] = hy,
                ["wavelength"] = wavelength,
                ["surface"] = surface,
                ["gridSize"] = gridSize
            }, system =>
            {
                int wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                if (wavelength > wavelengthCount)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength {wavelength} exceeds the system wavelength count ({wavelengthCount}).");

                int imageSurface = system.LDE.NumberOfSurfaces - 1;
                int target = surface == 0 ? imageSurface : surface;
                if (target < 1 || target > imageSurface)
                    throw new ArgumentOutOfRangeException(nameof(surface), $"Destination surface must be 1..{imageSurface}, or 0 for image surface.");

                // Use exactly the same normalized-pupil sampling core as
                // zemax_energy_budget and zemax_ray_footprint. This preserves
                // historical counts/normalization while preventing divergence
                // between three independent ray-loop implementations.
                var pupil = SequentialPupilSampler.CircularGrid(gridSize);
                var rayTrace = system.Tools.OpenBatchRayTrace()
                    ?? throw new InvalidOperationException("OpticStudio could not open the batch ray-trace tool.");
                SequentialPupilSampler.RaySample[] rays;
                try
                {
                    rays = SequentialPupilSampler.Trace(rayTrace, target, wavelength, hx, hy, pupil, cancellationToken);
                }
                finally { rayTrace.Close(); }

                int total = rays.Length;
                int clear = 0;
                int vignetted = 0;
                int errors = 0;
                double clearIntensity = 0;
                double tracedIntensity = 0;
                var vignetteCounts = new Dictionary<int, int>();
                foreach (var ray in rays)
                {
                    if (!ray.Valid)
                    {
                        errors++;
                        continue;
                    }
                    tracedIntensity += ray.Intensity;
                    if (ray.VignetteCode == 0)
                    {
                        clear++;
                        clearIntensity += ray.Intensity;
                    }
                    else
                    {
                        vignetted++;
                        vignetteCounts[ray.VignetteCode] =
                            vignetteCounts.TryGetValue(ray.VignetteCode, out var count) ? count + 1 : 1;
                    }
                }

                int successful = clear + vignetted;
                if (total == 0) throw new InvalidOperationException("The circular pupil grid contained no rays.");
                if (successful == 0) throw new InvalidOperationException($"All {total} pupil rays failed to trace; aperture throughput cannot be determined.");

                return new Result(
                    Success: true,
                    SurfaceNumber: target,
                    GridSize: gridSize,
                    TotalPupilRays: total,
                    SuccessfulRays: successful,
                    ClearRays: clear,
                    VignettedRays: vignetted,
                    ErrorRays: errors,
                    ClearFraction: (double)clear / successful,
                    IntensityWeightedFraction: tracedIntensity > 0 ? clearIntensity / tracedIntensity : 0,
                    VignetteBySurface: vignetteCounts.OrderBy(item => item.Key)
                        .Select(item => new VignetteCount(item.Key, item.Value))
                        .ToArray());
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, SurfaceNumber: surface, GridSize: gridSize);
        }
    }

    private static void ValidateNormalized(string name, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and in [-1, 1].");
    }
}
