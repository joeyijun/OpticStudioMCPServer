using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZOSAPI.Editors;
using ZOSAPI.Editors.LDE;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SetSurfaceSolveTool
{
    private readonly IZemaxSession _session;

    public SetSurfaceSolveTool(IZemaxSession session) => _session = session;

    public record SetSurfaceSolveResult(
        bool Success,
        string? Error,
        SolveData? NewSolveData
    );

    [ZemaxTool(Name = "zemax_set_surface_solve")]
    [Description("Set the solve type for a surface property (radius, thickness, conic, semiDiameter, material, or parameter)")]
    public async Task<SetSurfaceSolveResult> ExecuteAsync(
        [Description("Surface number")] int surfaceNumber,
        [Description("Property to set: radius, thickness, conic, semiDiameter, material, or param1-param8")] string property,
        [Description("Solve type: Fixed, Variable, Pickup, MarginalRayHeight, MarginalRayAngle, ChiefRayHeight, ChiefRayAngle, EdgeThickness, Position, FNumber, CenterOfCurvature, PupilPosition, MaterialSubstitute, MaterialOffset")] string solveType,
        [Description("For Pickup: source surface number")] int? pickupSurface = null,
        [Description("For Pickup: native ZOSAPI SurfaceColumn enum value; omit to retain the source property")] int? pickupColumn = null,
        [Description("For Pickup: scale factor")] double? scaleFactor = null,
        [Description("For Pickup: offset value")] double? offset = null,
        [Description("For MarginalRay/ChiefRay: height or angle value")] double? height = null,
        [Description("For MarginalRay: pupil zone (0-1)")] double? pupilZone = null,
        [Description("For EdgeThickness: thickness value")] double? thickness = null,
        [Description("For EdgeThickness: radial height")] double? radialHeight = null,
        [Description("For Position: distance from surface")] double? position = null,
        [Description("For FNumber: F/# value; when supplied it must be positive")] double? fNumber = null,
        [Description("For CenterOfCurvature/Position: reference surface")] int? referenceSurface = null,
        [Description("For MaterialSubstitute: catalog name")] string? catalog = null,
        [Description("Legacy parameter; MaterialSubstitute stores only a catalog. Omit and edit glass separately.")] string? materialName = null,
        [Description("For MaterialOffset: index offset")] double? indexOffset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(property))
                throw new ArgumentException("Property is required.", nameof(property));
            if (string.IsNullOrWhiteSpace(solveType))
                throw new ArgumentException("Solve type is required.", nameof(solveType));
            ValidateFinite(scaleFactor, nameof(scaleFactor));
            ValidateFinite(offset, nameof(offset));
            ValidateFinite(height, nameof(height));
            ValidateFinite(pupilZone, nameof(pupilZone));
            ValidateFinite(thickness, nameof(thickness));
            ValidateFinite(radialHeight, nameof(radialHeight));
            ValidateFinite(position, nameof(position));
            ValidateFinite(fNumber, nameof(fNumber));
            ValidateFinite(indexOffset, nameof(indexOffset));
            if (pupilZone.HasValue && (pupilZone.Value < 0 || pupilZone.Value > 1))
                throw new ArgumentOutOfRangeException(nameof(pupilZone), "Pupil zone must be between 0 and 1.");
            if (pickupColumn.HasValue && pickupColumn.Value < 0)
                throw new ArgumentOutOfRangeException(nameof(pickupColumn), "Pickup column cannot be negative.");
            if (fNumber.HasValue && fNumber.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(fNumber), "F-number must be positive when supplied.");

            var parameters = new Dictionary<string, object?>
            {
                ["surfaceNumber"] = surfaceNumber,
                ["property"] = property,
                ["solveType"] = solveType
            };

            var result = await _session.ExecuteAsync("SetSurfaceSolve", parameters, system =>
            {
                var lde = system.LDE;
                var surfNum = surfaceNumber == -1 ? lde.NumberOfSurfaces - 1 : surfaceNumber;

                if (surfNum < 0 || surfNum >= lde.NumberOfSurfaces)
                    throw new ArgumentException($"Invalid surface number: {surfaceNumber}. Valid range: 0-{lde.NumberOfSurfaces - 1}");
                if (pickupSurface.HasValue && (pickupSurface.Value < 0 || pickupSurface.Value >= lde.NumberOfSurfaces))
                    throw new ArgumentOutOfRangeException(nameof(pickupSurface), $"Pickup surface must be between 0 and {lde.NumberOfSurfaces - 1}.");
                if (referenceSurface.HasValue && (referenceSurface.Value < 0 || referenceSurface.Value >= lde.NumberOfSurfaces))
                    throw new ArgumentOutOfRangeException(nameof(referenceSurface), $"Reference surface must be between 0 and {lde.NumberOfSurfaces - 1}.");

                var row = lde.GetSurfaceAt(surfNum);
                IEditorCell? cell = GetCellForProperty(row, property.Trim());
                if (cell == null)
                    throw new ArgumentException($"Invalid property: {property}");

                ApplySolve(cell, solveType.Trim(), pickupSurface, pickupColumn, scaleFactor, offset,
                    height, pupilZone, thickness, radialHeight, position, fNumber,
                    referenceSurface, catalog, materialName, indexOffset);

                return new SetSurfaceSolveResult(
                    Success: true,
                    Error: null,
                    NewSolveData: GetSolveDataFromCell(cell, row.Material)
                );
            }, cancellationToken);

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SetSurfaceSolveResult(false, ex.Message, null);
        }
    }

    private static void ValidateFinite(double? value, string name)
    {
        if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
            throw new ArgumentException(name + " must be finite.", name);
    }

    private static IEditorCell? GetCellForProperty(ILDERow row, string property)
    {
        return property.ToLowerInvariant() switch
        {
            "radius" => row.RadiusCell,
            "thickness" => row.ThicknessCell,
            "conic" => row.ConicCell,
            "semidiameter" => row.SemiDiameterCell,
            "material" => row.MaterialCell,
            "param1" => row.GetSurfaceCell(SurfaceColumn.Par1),
            "param2" => row.GetSurfaceCell(SurfaceColumn.Par2),
            "param3" => row.GetSurfaceCell(SurfaceColumn.Par3),
            "param4" => row.GetSurfaceCell(SurfaceColumn.Par4),
            "param5" => row.GetSurfaceCell(SurfaceColumn.Par5),
            "param6" => row.GetSurfaceCell(SurfaceColumn.Par6),
            "param7" => row.GetSurfaceCell(SurfaceColumn.Par7),
            "param8" => row.GetSurfaceCell(SurfaceColumn.Par8),
            _ => null
        };
    }

    private static void ApplySolve(IEditorCell cell, string solveType,
        int? pickupSurface, int? pickupColumn, double? scaleFactor, double? offset,
        double? height, double? pupilZone, double? thickness, double? radialHeight,
        double? position, double? fNumber, int? referenceSurface,
        string? catalog, string? materialName, double? indexOffset)
    {
        var normalized = solveType.Equals("Pickup", StringComparison.OrdinalIgnoreCase) ? "SurfacePickup" : solveType;
        if (!Enum.TryParse<SolveType>(normalized, true, out var type) || !Enum.IsDefined(typeof(SolveType), type))
            throw new ArgumentException($"Unknown solve type: {solveType}");
        if (!cell.IsSolveTypeSupported(type))
            throw new ArgumentException($"Solve type {type} is not supported by this cell.");
        var data = cell.CreateSolveType(type);
        switch (type)
        {
            case SolveType.Fixed:
            case SolveType.Variable:
            case SolveType.PupilPosition:
                break;
            case SolveType.SurfacePickup:
                if (!pickupSurface.HasValue) throw new ArgumentException("Pickup requires pickupSurface.");
                var pickup = data._S_SurfacePickup;
                pickup.Surface = pickupSurface.Value;
                if (pickupColumn.HasValue)
                {
                    if (!Enum.IsDefined(typeof(SurfaceColumn), pickupColumn.Value))
                        throw new ArgumentException("pickupColumn must be a native SurfaceColumn enum value.");
                    pickup.Column = (SurfaceColumn)pickupColumn.Value;
                }
                if (pickup.SupportsScale) pickup.ScaleFactor = scaleFactor ?? 1;
                else if (scaleFactor.HasValue && scaleFactor.Value != 1)
                    throw new ArgumentException("This pickup does not support a scale factor.");
                if (pickup.SupportsOffset) pickup.Offset = offset ?? 0;
                else if (offset.HasValue && offset.Value != 0)
                    throw new ArgumentException("This pickup does not support an offset.");
                break;
            case SolveType.MarginalRayHeight:
                data._S_MarginalRayHeight.Height = height ?? 0;
                data._S_MarginalRayHeight.PupilZone = pupilZone ?? 1;
                break;
            case SolveType.MarginalRayAngle:
                if (pupilZone.HasValue) throw new ArgumentException("MarginalRayAngle has no pupil-zone parameter.");
                data._S_MarginalRayAngle.Angle = height ?? 0;
                break;
            case SolveType.ChiefRayHeight:
                data._S_ChiefRayHeight.Height = height ?? 0;
                break;
            case SolveType.ChiefRayAngle:
                data._S_ChiefRayAngle.Angle = height ?? 0;
                break;
            case SolveType.EdgeThickness:
                data._S_EdgeThickness.Thickness = thickness ?? 0;
                data._S_EdgeThickness.RadialHeight = radialHeight ?? 0;
                break;
            case SolveType.Position:
                if (!referenceSurface.HasValue) throw new ArgumentException("Position requires referenceSurface.");
                data._S_Position.FromSurface = referenceSurface.Value;
                data._S_Position.Length = position ?? 0;
                break;
            case SolveType.FNumber:
                if (!fNumber.HasValue || fNumber.Value <= 0) throw new ArgumentException("FNumber requires a positive fNumber.");
                data._S_FNumber.FNumber = fNumber.Value;
                break;
            case SolveType.CenterOfCurvature:
                if (!referenceSurface.HasValue) throw new ArgumentException("CenterOfCurvature requires referenceSurface.");
                data._S_CenterOfCurvature.RefSurface = referenceSurface.Value;
                break;
            case SolveType.MaterialSubstitute:
                if (string.IsNullOrWhiteSpace(catalog)) throw new ArgumentException("MaterialSubstitute requires catalog.");
                if (!string.IsNullOrWhiteSpace(materialName))
                    throw new ArgumentException("MaterialSubstitute stores a catalog only; edit material separately.");
                data._S_MaterialSubstitute.Catalog = catalog!.Trim();
                break;
            case SolveType.MaterialOffset:
                data._S_MaterialOffset.NdOffset = indexOffset ?? 0;
                break;
            default:
                throw new ArgumentException($"Unsupported solve type: {solveType}");
        }
        var status = cell.SetSolveData(data);
        if (status != SolveStatus.Success) throw new InvalidOperationException($"ZOS-API rejected the solve: {status}.");
    }

    private static SolveData GetSolveDataFromCell(IEditorCell cell, string? material = null)
        => SurfaceSolveReader.Read(cell.GetSolveData(), material);
}
