using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Editors.LDE;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SurfaceApertureTool
{
    private readonly IZemaxSession _session;
    public SurfaceApertureTool(IZemaxSession session) => _session = session;

    public record Result(bool Success, string? Error = null, int SurfaceNumber = 0, string? ApertureType = null,
        double? MinimumRadius = null, double? MaximumRadius = null, double? XDecenter = null, double? YDecenter = null);

    [ZemaxTool(Name = "zemax_set_surface_aperture")]
    [Description("Set a real sequential circular aperture or obscuration; unlike Semi-Diameter it terminates rays.")]
    public async Task<Result> SetAsync(
        [Description("Surface number")] int surfaceNumber,
        [Description("Aperture type: None, CircularAperture, CircularObscuration, or FloatingAperture")] string apertureType,
        [Description("Inner/minimum radius; must be finite and non-negative")] double minimumRadius = 0,
        [Description("Outer/maximum radius for circular aperture/obscuration; must be finite and positive")] double? maximumRadius = null,
        [Description("X decenter; must be finite")] double xDecenter = 0,
        [Description("Y decenter; must be finite")] double yDecenter = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(apertureType))
                return new Result(false, "apertureType is required.", surfaceNumber);
            if (!IsFinite(minimumRadius) || minimumRadius < 0)
                return new Result(false, "minimumRadius must be finite and non-negative.", surfaceNumber);
            if (maximumRadius.HasValue && (!IsFinite(maximumRadius.Value) || maximumRadius.Value <= 0))
                return new Result(false, "maximumRadius must be finite and positive when supplied.", surfaceNumber);
            if (!IsFinite(xDecenter) || !IsFinite(yDecenter))
                return new Result(false, "Aperture decenters must be finite.", surfaceNumber);

            var type = apertureType.Trim().ToLowerInvariant() switch
            {
                "none" => SurfaceApertureTypes.None,
                "circularaperture" => SurfaceApertureTypes.CircularAperture,
                "circularobscuration" => SurfaceApertureTypes.CircularObscuration,
                "floatingaperture" => SurfaceApertureTypes.FloatingAperture,
                _ => (SurfaceApertureTypes)(-1)
            };
            if ((int)type == -1)
                return new Result(false, $"Unsupported aperture type '{apertureType}'. Use None, CircularAperture, CircularObscuration, or FloatingAperture.", surfaceNumber);
            if (type is SurfaceApertureTypes.CircularAperture or SurfaceApertureTypes.CircularObscuration &&
                (!maximumRadius.HasValue || minimumRadius >= maximumRadius.Value))
                return new Result(false, "maximumRadius must be greater than minimumRadius for a circular aperture/obscuration.", surfaceNumber);

            return await _session.ExecuteAsync("SetSurfaceAperture",
                new Dictionary<string, object?>
                {
                    ["surfaceNumber"] = surfaceNumber,
                    ["apertureType"] = type.ToString(),
                    ["minimumRadius"] = minimumRadius,
                    ["maximumRadius"] = maximumRadius,
                    ["xDecenter"] = xDecenter,
                    ["yDecenter"] = yDecenter
                }, system =>
                {
                    var lde = system.LDE;
                    if (surfaceNumber < 0 || surfaceNumber >= lde.NumberOfSurfaces)
                        return new Result(false, $"Invalid surface number: {surfaceNumber}.", surfaceNumber);

                    var surface = lde.GetSurfaceAt(surfaceNumber);
                    var settings = surface.ApertureData.CreateApertureTypeSettings(type);
                    if (type is SurfaceApertureTypes.CircularAperture or SurfaceApertureTypes.CircularObscuration)
                    {
                        ISurfaceApertureCircular circular = type == SurfaceApertureTypes.CircularAperture
                            ? settings._S_CircularAperture
                            : settings._S_CircularObscuration;
                        circular.MinimumRadius = minimumRadius;
                        circular.MaximumRadius = maximumRadius!.Value;
                        circular.ApertureXDecenter = xDecenter;
                        circular.ApertureYDecenter = yDecenter;
                    }
                    surface.ApertureData.ChangeApertureTypeSettings(settings);
                    return Read(surfaceNumber, surface);
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, surfaceNumber);
        }
    }

    [ZemaxTool(Name = "zemax_get_surface_aperture")]
    [Description("Read the real sequential aperture or obscuration on a surface.")]
    public async Task<Result> GetAsync(
        [Description("Surface number")] int surfaceNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("GetSurfaceAperture",
                new Dictionary<string, object?> { ["surfaceNumber"] = surfaceNumber }, system =>
                {
                    var lde = system.LDE;
                    return surfaceNumber < 0 || surfaceNumber >= lde.NumberOfSurfaces
                        ? new Result(false, $"Invalid surface number: {surfaceNumber}.", surfaceNumber)
                        : Read(surfaceNumber, lde.GetSurfaceAt(surfaceNumber));
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, surfaceNumber);
        }
    }

    private static Result Read(int number, ILDERow surface)
    {
        var type = surface.ApertureData.CurrentType;
        if (type is not (SurfaceApertureTypes.CircularAperture or SurfaceApertureTypes.CircularObscuration))
            return new Result(true, SurfaceNumber: number, ApertureType: type.ToString());
        var settings = surface.ApertureData.CurrentTypeSettings;
        ISurfaceApertureCircular circular = type == SurfaceApertureTypes.CircularAperture
            ? settings._S_CircularAperture
            : settings._S_CircularObscuration;
        return new Result(true, SurfaceNumber: number, ApertureType: type.ToString(), MinimumRadius: circular.MinimumRadius,
            MaximumRadius: circular.MaximumRadius, XDecenter: circular.ApertureXDecenter, YDecenter: circular.ApertureYDecenter);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
