using ZemaxMCP.Core.Models;
using ZOSAPI;
using ZOSAPI.Editors;
using ZOSAPI.Editors.LDE;

namespace ZemaxMCP.Core.Services.ConstrainedOptimization;

public static class ZosVariableAccessor
{
    public static double GetVariableValue(IOpticalSystem system, OptVariable variable)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (variable == null) throw new ArgumentNullException(nameof(variable));
        var lde = system.LDE ?? throw new InvalidOperationException("Lens Data Editor is not available.");

        double value = variable.Type switch
        {
            VariableType.Curvature => GetCurvature(GetSurface(lde, variable.SurfaceNumber)),
            VariableType.Thickness => GetSurface(lde, variable.SurfaceNumber).Thickness,
            VariableType.Conic => GetSurface(lde, variable.SurfaceNumber).Conic,
            VariableType.Parameter => GetParameter(GetSurface(lde, variable.SurfaceNumber), variable.ParameterNumber),
            VariableType.FieldX => GetField(system, variable.FieldNumber).X,
            VariableType.FieldY => GetField(system, variable.FieldNumber).Y,
            VariableType.ConfigOperand => GetMceVariableCell(system, variable).DoubleValue,
            VariableType.ModelNd or VariableType.ModelVd or VariableType.ModelDpgF =>
                GetModelGlassValue(GetSurface(lde, variable.SurfaceNumber), variable.Type),
            _ => throw new ArgumentException($"Unknown variable type: {variable.Type}", nameof(variable))
        };
        ValidateFinite(value, variable.Description);
        return value;
    }

    public static void SetVariableValue(IOpticalSystem system, OptVariable variable, double value)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (variable == null) throw new ArgumentNullException(nameof(variable));
        ValidateFinite(value, variable.Description);
        var lde = system.LDE ?? throw new InvalidOperationException("Lens Data Editor is not available.");

        switch (variable.Type)
        {
            case VariableType.Curvature:
                // OpticStudio represents a plane as Radius=0 (infinite radius).
                GetSurface(lde, variable.SurfaceNumber).Radius = value == 0 ? 0 : 1.0 / value;
                break;
            case VariableType.Thickness:
                GetSurface(lde, variable.SurfaceNumber).Thickness = value;
                break;
            case VariableType.Conic:
                GetSurface(lde, variable.SurfaceNumber).Conic = value;
                break;
            case VariableType.Parameter:
                GetParameterCell(GetSurface(lde, variable.SurfaceNumber), variable.ParameterNumber).DoubleValue = value;
                break;
            case VariableType.FieldX:
                GetField(system, variable.FieldNumber).X = value;
                break;
            case VariableType.FieldY:
                GetField(system, variable.FieldNumber).Y = value;
                break;
            case VariableType.ConfigOperand:
                GetMceVariableCell(system, variable).DoubleValue = value;
                break;
            case VariableType.ModelNd:
            case VariableType.ModelVd:
            case VariableType.ModelDpgF:
                SetModelGlassValue(GetSurface(lde, variable.SurfaceNumber), variable.Type, value);
                break;
            default:
                throw new ArgumentException($"Unknown variable type: {variable.Type}", nameof(variable));
        }

        var applied = GetVariableValue(system, variable);
        if (!ApproximatelyEqual(applied, value))
            throw new InvalidOperationException($"OpticStudio did not preserve {variable.Description}: requested {value:G17}, read back {applied:G17}.");
    }

    public static string GetGlassMaterial(IOpticalSystem system, int surfaceIndex)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        return GetSurface(system.LDE, surfaceIndex).Material;
    }

    public static void SetGlassMaterial(IOpticalSystem system, int surfaceIndex, string materialName)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        if (string.IsNullOrWhiteSpace(materialName))
            throw new ArgumentException("materialName cannot be empty.", nameof(materialName));

        var surface = GetSurface(system.LDE, surfaceIndex);
        var solveData = surface.MaterialCell.GetSolveData()
            ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} did not return material solve data.");

        if (solveData.Type == SolveType.MaterialSubstitute)
        {
            var sub = solveData._S_MaterialSubstitute;
            string catalog = sub != null ? sub.Catalog : surface.MaterialCatalog;
            surface.Material = materialName;
            var newSolve = surface.MaterialCell.CreateSolveType(SolveType.MaterialSubstitute)
                ?? throw new InvalidOperationException("OpticStudio could not create a MaterialSubstitute solve.");
            var newSub = newSolve._S_MaterialSubstitute
                ?? throw new InvalidOperationException("OpticStudio did not expose typed MaterialSubstitute solve data.");
            newSub.Catalog = catalog;
            var status = surface.MaterialCell.SetSolveData(newSolve);
            if (status != SolveStatus.Success)
                throw new InvalidOperationException($"OpticStudio rejected MaterialSubstitute solve restoration with status {status}.");
        }
        else
        {
            surface.Material = materialName;
        }

        if (!string.Equals(surface.Material, materialName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"OpticStudio did not preserve material '{materialName}' on surface {surface.SurfaceNumber}; read back '{surface.Material}'.");
    }

    private static ILDERow GetSurface(ZOSAPI.Editors.LDE.ILensDataEditor lde, int surfaceIndex)
    {
        if (surfaceIndex < 0 || surfaceIndex >= lde.NumberOfSurfaces)
            throw new ArgumentOutOfRangeException(nameof(surfaceIndex), $"Surface index {surfaceIndex} is outside 0..{lde.NumberOfSurfaces - 1}.");
        return lde.GetSurfaceAt(surfaceIndex)
            ?? throw new InvalidOperationException($"OpticStudio returned no LDE row for surface {surfaceIndex}.");
    }

    private static dynamic GetField(IOpticalSystem system, int fieldNumber)
    {
        var fields = system.SystemData?.Fields ?? throw new InvalidOperationException("Field data is not available.");
        if (fieldNumber < 1 || fieldNumber > fields.NumberOfFields)
            throw new ArgumentOutOfRangeException(nameof(fieldNumber), $"Field {fieldNumber} is outside 1..{fields.NumberOfFields}.");
        return fields.GetField(fieldNumber);
    }

    private static double GetCurvature(ILDERow surface)
    {
        double r = surface.Radius;
        ValidateFinite(r, $"surface {surface.SurfaceNumber} radius");
        return r == 0 ? 0 : 1.0 / r;
    }

    private static double GetParameter(ILDERow surface, int parameterNumber) =>
        GetParameterCell(surface, parameterNumber).DoubleValue;

    private static IEditorCell GetParameterCell(ILDERow surface, int parameterNumber)
    {
        if (parameterNumber < 1 || parameterNumber > 40)
            throw new ArgumentOutOfRangeException(nameof(parameterNumber), "Surface parameter number must be in 1..40.");
        var col = SurfaceColumn.Par0 + parameterNumber;
        var cell = surface.GetSurfaceCell(col)
            ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} did not expose parameter {parameterNumber}.");
        if (!cell.IsActive)
            throw new InvalidOperationException($"Surface {surface.SurfaceNumber} parameter {parameterNumber} is not active.");
        if (cell.DataType != CellDataType.Double)
            throw new InvalidOperationException($"Surface {surface.SurfaceNumber} parameter {parameterNumber} has data type {cell.DataType}; constrained optimization supports Double variables only.");
        return cell;
    }

    private static IEditorCell GetMceVariableCell(IOpticalSystem system, OptVariable variable)
    {
        var mce = system.MCE ?? throw new InvalidOperationException("Multi-Configuration Editor is not available.");
        if (variable.ConfigOperandRow < 1 || variable.ConfigOperandRow > mce.NumberOfOperands)
            throw new ArgumentOutOfRangeException(nameof(variable.ConfigOperandRow), $"MCE row {variable.ConfigOperandRow} is outside 1..{mce.NumberOfOperands}.");
        if (variable.ConfigColumn < 1 || variable.ConfigColumn > mce.NumberOfConfigurations)
            throw new ArgumentOutOfRangeException(nameof(variable.ConfigColumn), $"MCE configuration {variable.ConfigColumn} is outside 1..{mce.NumberOfConfigurations}.");
        var row = mce.GetOperandAt(variable.ConfigOperandRow)
            ?? throw new InvalidOperationException($"OpticStudio returned no MCE row {variable.ConfigOperandRow}.");
        var cell = row.GetOperandCell(variable.ConfigColumn)
            ?? throw new InvalidOperationException($"MCE row {variable.ConfigOperandRow}, configuration {variable.ConfigColumn} returned no operand cell.");
        if (!cell.IsActive)
            throw new InvalidOperationException($"MCE row {variable.ConfigOperandRow}, configuration {variable.ConfigColumn} is not active.");
        if (cell.DataType != CellDataType.Double)
            throw new InvalidOperationException($"MCE row {variable.ConfigOperandRow}, configuration {variable.ConfigColumn} uses {cell.DataType}; constrained optimization supports Double configuration variables only.");
        return cell;
    }

    private static double GetModelGlassValue(ILDERow surface, VariableType type)
    {
        var solveData = surface.MaterialCell.GetSolveData()
            ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} did not return material solve data.");
        if (solveData.Type != SolveType.MaterialModel)
            throw new InvalidOperationException($"Surface {surface.SurfaceNumber} no longer uses a MaterialModel solve.");
        var model = solveData._S_MaterialModel
            ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} did not expose typed MaterialModel solve data.");
        return type switch
        {
            VariableType.ModelNd => model.IndexNd,
            VariableType.ModelVd => model.AbbeVd,
            VariableType.ModelDpgF => model.dPgF,
            _ => throw new ArgumentException($"Variable type {type} is not a model-glass variable.", nameof(type))
        };
    }

    private static void SetModelGlassValue(ILDERow surface, VariableType type, double value)
    {
        var solveData = surface.MaterialCell.GetSolveData()
            ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} did not return material solve data.");
        if (solveData.Type != SolveType.MaterialModel)
            throw new InvalidOperationException($"Surface {surface.SurfaceNumber} no longer uses a MaterialModel solve.");
        var model = solveData._S_MaterialModel
            ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} did not expose typed MaterialModel solve data.");

        double nd = model.IndexNd;
        double vd = model.AbbeVd;
        double dpgf = model.dPgF;
        bool varyNd = model.VaryIndex;
        bool varyVd = model.VaryAbbe;
        bool varyDpgf = model.VarydPgF;

        switch (type)
        {
            case VariableType.ModelNd: nd = value; break;
            case VariableType.ModelVd: vd = value; break;
            case VariableType.ModelDpgF: dpgf = value; break;
            default: throw new ArgumentException($"Variable type {type} is not a model-glass variable.", nameof(type));
        }

        var newSolve = surface.MaterialCell.CreateSolveType(SolveType.MaterialModel)
            ?? throw new InvalidOperationException("OpticStudio could not create a MaterialModel solve.");
        var newModel = newSolve._S_MaterialModel
            ?? throw new InvalidOperationException("OpticStudio did not expose typed MaterialModel solve data on the new solve.");
        newModel.IndexNd = nd;
        newModel.AbbeVd = vd;
        newModel.dPgF = dpgf;
        newModel.VaryIndex = varyNd;
        newModel.VaryAbbe = varyVd;
        newModel.VarydPgF = varyDpgf;
        var status = surface.MaterialCell.SetSolveData(newSolve);
        if (status != SolveStatus.Success)
            throw new InvalidOperationException($"OpticStudio rejected MaterialModel solve update with status {status}.");
    }

    private static void ValidateFinite(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"Constrained optimization encountered non-finite {label}: {value}.");
    }

    private static bool ApproximatelyEqual(double a, double b)
    {
        double scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= 1e-10 * scale;
    }
}
