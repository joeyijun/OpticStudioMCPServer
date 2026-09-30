using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI.Tools.RayTrace;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public sealed class RayTraceDiagnosticsTool
{
    private const int MaximumSampledRays = 500;
    private readonly IZemaxSession _session;

    public RayTraceDiagnosticsTool(IZemaxSession session) => _session = session;

    public record SamplePoint(double X, double Y);

    public record Failure(
        int Wavelength,
        double Hx,
        double Hy,
        double Px,
        double Py,
        int TargetSurface,
        bool ApiSuccess,
        int ErrorCode,
        int VignetteCode,
        int? FirstProblemSurface);

    public record Result(
        bool Success,
        string? Error,
        int TargetSurface,
        int TotalRays,
        int ValidRays,
        int ClearRays,
        int ProblemRays,
        IReadOnlyList<int> Wavelengths,
        IReadOnlyList<SamplePoint> FieldSamples,
        IReadOnlyList<SamplePoint> PupilSamples,
        IReadOnlyDictionary<int, int> ErrorCodeCounts,
        IReadOnlyDictionary<int, int> VignetteCodeCounts,
        IReadOnlyDictionary<int, int> FirstProblemSurfaceCounts,
        IReadOnlyList<Failure> Failures,
        bool FailuresTruncated);

    [ZemaxTool(Name = "zemax_ray_trace_diagnostics")]
    [Description("Run a bounded grid of normalized real-ray traces and return evidence-based failure/vignetting diagnostics. For problematic rays, re-traces progressively through the LDE to identify the first surface where the API reports an error or vignette code. Read-only.")]
    public async Task<Result> ExecuteAsync(
        [Description("Normalized field sampling pattern: 1=center, 5=center+cardinal edge points, 9=3x3 grid")] int fieldSampling = 5,
        [Description("Normalized pupil sampling pattern: 1=center, 5=center+cardinal edge points, 9=3x3 grid")] int pupilSampling = 5,
        [Description("Trace every configured wavelength instead of only the primary wavelength")] bool allWavelengths = false,
        [Description("Surface to trace to; 0 means image surface")] int surface = 0,
        [Description("Maximum individual problem rays to return (1-100)")] int maxFailures = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fieldPoints = BuildSamples(fieldSampling, nameof(fieldSampling));
            var pupilPoints = BuildSamples(pupilSampling, nameof(pupilSampling));
            if (surface < 0)
                throw new ArgumentOutOfRangeException(nameof(surface), "surface must be 0 (image) or a positive LDE surface number.");
            if (maxFailures is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(maxFailures), "maxFailures must be between 1 and 100.");

            return await _session.ExecuteAsync(
                "RayTraceDiagnostics",
                new Dictionary<string, object?>
                {
                    ["fieldSampling"] = fieldSampling,
                    ["pupilSampling"] = pupilSampling,
                    ["allWavelengths"] = allWavelengths,
                    ["surface"] = surface,
                    ["maxFailures"] = maxFailures
                },
                system =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lastSurface = system.LDE.NumberOfSurfaces - 1;
                    var targetSurface = surface == 0 ? lastSurface : surface;
                    if (targetSurface < 1 || targetSurface > lastSurface)
                        throw new ArgumentOutOfRangeException(nameof(surface), $"surface must be 0 (image) or between 1 and {lastSurface}.");

                    var wavelengthTable = system.SystemData.Wavelengths;
                    var wavelengthCount = wavelengthTable.NumberOfWavelengths;
                    if (wavelengthCount < 1)
                        throw new InvalidDataException("The optical system contains no wavelengths.");

                    var wavelengths = new List<int>();
                    if (allWavelengths)
                    {
                        if (wavelengthCount > 20)
                            throw new InvalidOperationException($"The system has {wavelengthCount} wavelengths; allWavelengths is limited to 20 for bounded diagnostics.");
                        for (var index = 1; index <= wavelengthCount; index++) wavelengths.Add(index);
                    }
                    else
                    {
                        var primary = 1;
                        for (var index = 1; index <= wavelengthCount; index++)
                        {
                            if (!wavelengthTable.GetWavelength(index).IsPrimary) continue;
                            primary = index;
                            break;
                        }
                        wavelengths.Add(primary);
                    }

                    var totalRequested = checked(fieldPoints.Count * pupilPoints.Count * wavelengths.Count);
                    if (totalRequested > MaximumSampledRays)
                        throw new InvalidOperationException(
                            $"Requested diagnostic grid contains {totalRequested} rays; the maximum is {MaximumSampledRays}. Reduce sampling or use only the primary wavelength.");

                    var errorCounts = new Dictionary<int, int>();
                    var vignetteCounts = new Dictionary<int, int>();
                    var surfaceCounts = new Dictionary<int, int>();
                    var failures = new List<Failure>(Math.Min(maxFailures, totalRequested));
                    var total = 0;
                    var valid = 0;
                    var clear = 0;
                    var problems = 0;

                    var batch = system.Tools.OpenBatchRayTrace()
                        ?? throw new InvalidOperationException("OpticStudio did not open Batch Ray Trace.");
                    try
                    {
                        foreach (var wavelength in wavelengths)
                        foreach (var field in fieldPoints)
                        foreach (var pupil in pupilPoints)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            total++;
                            var trace = Trace(batch, targetSurface, wavelength, field, pupil);
                            if (trace.ApiSuccess && trace.ErrorCode == 0) valid++;
                            if (trace.ApiSuccess && trace.ErrorCode == 0 && trace.VignetteCode == 0) clear++;

                            Increment(errorCounts, trace.ErrorCode);
                            Increment(vignetteCounts, trace.VignetteCode);

                            if (trace.ApiSuccess && trace.ErrorCode == 0 && trace.VignetteCode == 0)
                            {
                                ValidateFiniteTrace(trace, wavelength, field, pupil);
                                continue;
                            }

                            problems++;
                            var firstProblemSurface = LocateFirstProblemSurface(
                                batch, targetSurface, wavelength, field, pupil, cancellationToken);
                            if (firstProblemSurface.HasValue) Increment(surfaceCounts, firstProblemSurface.Value);

                            if (failures.Count < maxFailures)
                            {
                                failures.Add(new Failure(
                                    wavelength,
                                    field.X,
                                    field.Y,
                                    pupil.X,
                                    pupil.Y,
                                    targetSurface,
                                    trace.ApiSuccess,
                                    trace.ErrorCode,
                                    trace.VignetteCode,
                                    firstProblemSurface));
                            }
                        }
                    }
                    finally
                    {
                        batch.Close();
                    }

                    return new Result(
                        true,
                        null,
                        targetSurface,
                        total,
                        valid,
                        clear,
                        problems,
                        wavelengths,
                        fieldPoints,
                        pupilPoints,
                        errorCounts,
                        vignetteCounts,
                        surfaceCounts,
                        failures,
                        problems > failures.Count);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(
                false,
                ex.Message,
                surface,
                0,
                0,
                0,
                0,
                Array.Empty<int>(),
                Array.Empty<SamplePoint>(),
                Array.Empty<SamplePoint>(),
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
                new Dictionary<int, int>(),
                Array.Empty<Failure>(),
                false);
        }
    }

    private static List<SamplePoint> BuildSamples(int sampling, string parameterName) => sampling switch
    {
        1 => new List<SamplePoint> { new(0, 0) },
        5 => new List<SamplePoint> { new(0, 0), new(-1, 0), new(1, 0), new(0, -1), new(0, 1) },
        9 => new List<SamplePoint>
        {
            new(-1, -1), new(0, -1), new(1, -1),
            new(-1, 0),  new(0, 0),  new(1, 0),
            new(-1, 1),  new(0, 1),  new(1, 1)
        },
        _ => throw new ArgumentOutOfRangeException(parameterName, "Sampling must be exactly 1, 5, or 9.")
    };

    private static TraceResult Trace(
        IBatchRayTrace batch,
        int surface,
        int wavelength,
        SamplePoint field,
        SamplePoint pupil)
    {
        var apiSuccess = batch.SingleRayNormUnpol(
            RaysType.Real,
            surface,
            wavelength,
            field.X,
            field.Y,
            pupil.X,
            pupil.Y,
            false,
            out var error,
            out var vignette,
            out var x,
            out var y,
            out var z,
            out var l,
            out var m,
            out var n,
            out _,
            out _,
            out _,
            out var opd,
            out var intensity);

        return new TraceResult(apiSuccess, error, vignette, x, y, z, l, m, n, opd, intensity);
    }

    private static int? LocateFirstProblemSurface(
        IBatchRayTrace batch,
        int targetSurface,
        int wavelength,
        SamplePoint field,
        SamplePoint pupil,
        CancellationToken cancellationToken)
    {
        for (var currentSurface = 1; currentSurface <= targetSurface; currentSurface++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trace = Trace(batch, currentSurface, wavelength, field, pupil);
            if (!trace.ApiSuccess || trace.ErrorCode != 0 || trace.VignetteCode != 0)
                return currentSurface;
        }
        return null;
    }

    private static void Increment(Dictionary<int, int> counts, int code) =>
        counts[code] = counts.TryGetValue(code, out var count) ? count + 1 : 1;

    private static void ValidateFiniteTrace(TraceResult trace, int wavelength, SamplePoint field, SamplePoint pupil)
    {
        var values = new[] { trace.X, trace.Y, trace.Z, trace.L, trace.M, trace.N, trace.Opd, trace.Intensity };
        if (values.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
            throw new InvalidDataException(
                $"A clear ray returned non-finite data at wavelength {wavelength}, field ({field.X},{field.Y}), pupil ({pupil.X},{pupil.Y}).");
    }

    private sealed record TraceResult(
        bool ApiSuccess,
        int ErrorCode,
        int VignetteCode,
        double X,
        double Y,
        double Z,
        double L,
        double M,
        double N,
        double Opd,
        double Intensity);
}
