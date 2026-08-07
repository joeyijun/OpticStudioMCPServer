using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SetSurfaceTool
{
    private readonly IZemaxSession _session;

    public SetSurfaceTool(IZemaxSession session) => _session = session;

    public record SetSurfaceResult(
        bool Success,
        string? Error,
        Surface UpdatedSurface,
        List<string>? Warnings = null
    );

    [ZemaxTool(Name = "zemax_set_surface")]
    [Description("Modify properties of a surface in the lens data editor. Omitted nullable arguments are left unchanged; explicit false/empty-string values clear the corresponding stop/solve/text state.")]
    public async Task<SetSurfaceResult> ExecuteAsync(
        [Description("Surface number to modify")] int surfaceNumber,
        [Description("Radius of curvature")] double? radius = null,
        [Description("Thickness to next surface")] double? thickness = null,
        [Description("Material/glass name. Pass an empty string to clear the material back to AIR.")] string? material = null,
        [Description("Semi-diameter")] double? semiDiameter = null,
        [Description("Conic constant")] double? conic = null,
        [Description("Surface comment. Pass an empty string to clear the comment.")] string? comment = null,
        [Description("Set or clear stop status. true makes this the stop surface; false clears stop status; omit to leave unchanged.")] bool? isStop = null,
        [Description("Radius solve state. true makes radius Variable; false makes it Fixed; omit to leave unchanged.")] bool? radiusVariable = null,
        [Description("Thickness solve state. true makes thickness Variable; false makes it Fixed; omit to leave unchanged.")] bool? thicknessVariable = null,
        [Description("Conic solve state. true makes conic Variable; false makes it Fixed; omit to leave unchanged.")] bool? conicVariable = null,
        [Description("Minimum bound for thickness variable. Hard constraint the optimizer cannot violate. Requires OpticStudio 2023+.")] double? thicknessMin = null,
        [Description("Maximum bound for thickness variable. Hard constraint the optimizer cannot violate. Requires OpticStudio 2023+.")] double? thicknessMax = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (thicknessMin.HasValue && thicknessMax.HasValue && thicknessMin.Value > thicknessMax.Value)
                throw new ArgumentException("thicknessMin cannot be greater than thicknessMax.");

            var parameters = new Dictionary<string, object?>
            {
                ["surfaceNumber"] = surfaceNumber,
                ["radius"] = radius,
                ["thickness"] = thickness,
                ["material"] = material,
                ["semiDiameter"] = semiDiameter,
                ["conic"] = conic,
                ["comment"] = comment,
                ["isStop"] = isStop,
                ["radiusVariable"] = radiusVariable,
                ["thicknessVariable"] = thicknessVariable,
                ["conicVariable"] = conicVariable,
                ["thicknessMin"] = thicknessMin,
                ["thicknessMax"] = thicknessMax
            };

            var result = await _session.ExecuteAsync("SetSurface", parameters, system =>
            {
                var lde = system.LDE;

                if (surfaceNumber < 0 || surfaceNumber >= lde.NumberOfSurfaces)
                {
                    throw new ArgumentException(
                        $"Invalid surface number: {surfaceNumber}. " +
                        $"Valid range: 0-{lde.NumberOfSurfaces - 1}");
                }

                var surface = lde.GetSurfaceAt(surfaceNumber);

                if (radius.HasValue)
                    surface.Radius = radius.Value;

                if (thickness.HasValue)
                    surface.Thickness = thickness.Value;

                // null means "leave unchanged"; an explicit empty string is a
                // meaningful Zemax value that clears the material back to AIR.
                if (material is not null)
                    surface.Material = material;

                if (semiDiameter.HasValue)
                    surface.SemiDiameter = semiDiameter.Value;

                if (conic.HasValue)
                    surface.Conic = conic.Value;

                // Preserve the distinction between omitted and explicitly empty.
                if (comment is not null)
                    surface.Comment = comment;

                if (isStop.HasValue)
                    surface.IsStop = isStop.Value;

                ApplyVariableSolve(surface.RadiusCell, radiusVariable);
                ApplyVariableSolve(surface.ThicknessCell, thicknessVariable);
                ApplyVariableSolve(surface.ConicCell, conicVariable);

                // Set variable bounds (requires OpticStudio 2023+ API)
                var boundsWarnings = new List<string>();
                if (thicknessMin.HasValue || thicknessMax.HasValue)
                {
                    try
                    {
                        dynamic cell = surface.ThicknessCell;
                        if (thicknessMin.HasValue)
                            cell.Min = thicknessMin.Value;
                        if (thicknessMax.HasValue)
                            cell.Max = thicknessMax.Value;
                    }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                    {
                        boundsWarnings.Add(
                            "ThicknessCell.Min/Max properties not available in this OpticStudio version. " +
                            "Variable bounds require OpticStudio 2023 or later. " +
                            "Consider using MNCT/MXCT merit function operands as an alternative.");
                    }
                }

                return new SetSurfaceResult(
                    Success: true,
                    Error: null,
                    UpdatedSurface: new Surface
                    {
                        Number = surfaceNumber,
                        Comment = surface.Comment ?? "",
                        Radius = surface.Radius,
                        Thickness = surface.Thickness,
                        Material = surface.Material,
                        SemiDiameter = surface.SemiDiameter,
                        Conic = surface.Conic,
                        SurfaceType = surface.Type.ToString(),
                        IsStop = surface.IsStop
                    },
                    Warnings: boundsWarnings.Count > 0 ? boundsWarnings : null
                );
            }, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            return new SetSurfaceResult(
                Success: false,
                Error: ex.Message,
                UpdatedSurface: new Surface { Number = surfaceNumber }
            );
        }
    }

    private static void ApplyVariableSolve(dynamic cell, bool? variable)
    {
        if (!variable.HasValue) return;
        if (!variable.Value)
        {
            cell.MakeSolveFixed();
            return;
        }

        var solveType = cell.Solve;
        if (solveType != ZOSAPI.Editors.SolveType.Fixed &&
            solveType != ZOSAPI.Editors.SolveType.Variable)
        {
            cell.MakeSolveFixed();
        }
        cell.MakeSolveVariable();
    }
}
