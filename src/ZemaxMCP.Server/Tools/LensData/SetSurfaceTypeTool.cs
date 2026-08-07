using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SetSurfaceTypeTool
{
    private readonly IZemaxSession _session;

    public SetSurfaceTypeTool(IZemaxSession session) => _session = session;

    public record SetSurfaceTypeResult(
        bool Success,
        string? Error = null,
        int SurfaceNumber = 0,
        string? PreviousType = null,
        string? NewType = null,
        string[]? AvailableTypes = null);

    [ZemaxTool(Name = "zemax_set_surface_type")]
    [Description(
        "Change a surface type (for example Standard, CoordinateBreak, EvenAspheric, Toroidal, or Biconic). "
        + "Use zemax_list_surface_types for read-only discovery. listTypes=true remains as a compatibility path and returns the static enum without modifying OpticStudio. "
        + "After changing type, use zemax_set_surface_parameter to set type-specific PARM values.")]
    public async Task<SetSurfaceTypeResult> ExecuteAsync(
        [Description("Surface number to modify")] int surfaceNumber,
        [Description("Surface type name; use zemax_list_surface_types to discover names")] string? surfaceType = null,
        [Description("Compatibility option: when true, return the static surface-type list without modifying the system")] bool listTypes = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (listTypes)
            {
                var names = Enum.GetNames(typeof(ZOSAPI.Editors.LDE.SurfaceType));
                Array.Sort(names, StringComparer.OrdinalIgnoreCase);
                return new SetSurfaceTypeResult(
                    Success: true,
                    SurfaceNumber: surfaceNumber,
                    AvailableTypes: names);
            }

            if (string.IsNullOrWhiteSpace(surfaceType))
                return new SetSurfaceTypeResult(false, Error: "surfaceType is required when listTypes=false");

            var enumName = Enum.GetNames(typeof(ZOSAPI.Editors.LDE.SurfaceType))
                .FirstOrDefault(name => string.Equals(name, surfaceType.Trim(), StringComparison.OrdinalIgnoreCase));
            if (enumName == null)
                return new SetSurfaceTypeResult(false,
                    Error: $"Unknown surface type: '{surfaceType}'. Use zemax_list_surface_types to see valid names.");
            var targetType = (ZOSAPI.Editors.LDE.SurfaceType)Enum.Parse(typeof(ZOSAPI.Editors.LDE.SurfaceType), enumName, ignoreCase: false);

            var parameters = new Dictionary<string, object?>
            {
                ["surfaceNumber"] = surfaceNumber,
                ["surfaceType"] = enumName
            };

            return await _session.ExecuteAsync("SetSurfaceType", parameters, system =>
            {
                var lde = system.LDE;
                if (surfaceNumber < 0 || surfaceNumber >= lde.NumberOfSurfaces)
                    throw new ArgumentException($"Invalid surface number: {surfaceNumber}. Valid range: 0-{lde.NumberOfSurfaces - 1}");

                var surface = lde.GetSurfaceAt(surfaceNumber);
                var previousType = surface.Type.ToString();
                try
                {
                    dynamic dynSurface = surface;
                    var typeSettings = dynSurface.GetSurfaceTypeSettings(targetType);
                    dynSurface.ChangeType(typeSettings);

                    var newType = surface.Type.ToString();
                    if (!string.Equals(newType, enumName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new SetSurfaceTypeResult(false,
                            Error: $"Surface {surfaceNumber} type unchanged or resolved to '{newType}' instead of requested '{enumName}'. Object/image surfaces and some licensed surface types may reject the change.",
                            SurfaceNumber: surfaceNumber,
                            PreviousType: previousType,
                            NewType: newType);
                    }

                    return new SetSurfaceTypeResult(
                        Success: true,
                        SurfaceNumber: surfaceNumber,
                        PreviousType: previousType,
                        NewType: newType);
                }
                catch (Exception ex)
                {
                    return new SetSurfaceTypeResult(false,
                        Error: $"Failed to change surface type: {ex.Message}",
                        SurfaceNumber: surfaceNumber,
                        PreviousType: previousType,
                        NewType: surface.Type.ToString());
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SetSurfaceTypeResult(false, Error: ex.Message);
        }
    }
}
