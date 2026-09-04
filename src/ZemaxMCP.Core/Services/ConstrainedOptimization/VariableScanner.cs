using ZemaxMCP.Core.Models;
using ZOSAPI;
using ZOSAPI.Editors;
using ZOSAPI.Editors.LDE;

namespace ZemaxMCP.Core.Services.ConstrainedOptimization;

public class VariableScanner
{
    public List<OptVariable> ScanVariables(IOpticalSystem system, CancellationToken cancellationToken = default)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        var variables = new List<OptVariable>();
        int varNum = 1;

        var lde = system.LDE ?? throw new InvalidOperationException("Lens Data Editor is not available.");
        int numSurfaces = lde.NumberOfSurfaces;

        for (int i = 0; i < numSurfaces; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ILDERow surface = lde.GetSurfaceAt(i)
                ?? throw new InvalidOperationException($"OpticStudio returned no LDE row for surface {i}.");
            int surfNum = surface.SurfaceNumber;

            if (surface.RadiusCell.Solve == SolveType.Variable)
            {
                double r = surface.Radius;
                ValidateFinite(r, $"surface {surfNum} radius");
                double c = r == 0 ? 0 : 1.0 / r;
                ValidateFinite(c, $"surface {surfNum} curvature");
                variables.Add(NewVariable(ref varNum, $"Surface {surfNum} Curvature", VariableType.Curvature, c, surfaceNumber: i));
            }

            if (surface.ThicknessCell.Solve == SolveType.Variable)
            {
                double value = surface.Thickness;
                ValidateFinite(value, $"surface {surfNum} thickness");
                variables.Add(NewVariable(ref varNum, $"Surface {surfNum} Thickness", VariableType.Thickness, value, surfaceNumber: i));
            }

            if (surface.ConicCell.Solve == SolveType.Variable)
            {
                double value = surface.Conic;
                ValidateFinite(value, $"surface {surfNum} conic");
                variables.Add(NewVariable(ref varNum, $"Surface {surfNum} Conic", VariableType.Conic, value, surfaceNumber: i));
            }

            for (int p = 1; p <= 40; p++)
            {
                if ((p & 7) == 0) cancellationToken.ThrowIfCancellationRequested();
                SurfaceColumn col = SurfaceColumn.Par0 + p;
                var cell = surface.GetSurfaceCell(col)
                    ?? throw new InvalidOperationException($"Surface {surfNum} did not expose parameter cell {p}.");
                if (cell.IsActive && cell.Solve == SolveType.Variable)
                {
                    if (cell.DataType != CellDataType.Double)
                        throw new InvalidDataException($"Surface {surfNum} parameter {p} is variable but has unsupported data type {cell.DataType}; constrained optimization supports numeric Double variables only.");
                    double value = cell.DoubleValue;
                    ValidateFinite(value, $"surface {surfNum} parameter {p}");
                    variables.Add(NewVariable(ref varNum, $"Surface {surfNum} Param {p}", VariableType.Parameter, value,
                        surfaceNumber: i, parameterNumber: p));
                }
            }

            var materialSolve = surface.MaterialCell.GetSolveData()
                ?? throw new InvalidOperationException($"Surface {surfNum} did not return material solve data.");
            if (materialSolve.Type == SolveType.MaterialModel)
            {
                var model = materialSolve._S_MaterialModel
                    ?? throw new InvalidOperationException($"Surface {surfNum} uses MaterialModel but typed model solve data is unavailable.");
                if (model.VaryIndex)
                {
                    ValidateFinite(model.IndexNd, $"surface {surfNum} model Nd");
                    variables.Add(NewVariable(ref varNum, $"Surface {surfNum} Model Nd", VariableType.ModelNd, model.IndexNd, surfaceNumber: i));
                }
                if (model.VaryAbbe)
                {
                    ValidateFinite(model.AbbeVd, $"surface {surfNum} model Vd");
                    variables.Add(NewVariable(ref varNum, $"Surface {surfNum} Model Vd", VariableType.ModelVd, model.AbbeVd, surfaceNumber: i));
                }
                if (model.VarydPgF)
                {
                    ValidateFinite(model.dPgF, $"surface {surfNum} model dPgF");
                    variables.Add(NewVariable(ref varNum, $"Surface {surfNum} Model dPgF", VariableType.ModelDpgF, model.dPgF, surfaceNumber: i));
                }
            }
        }

        var fields = system.SystemData?.Fields ?? throw new InvalidOperationException("Field data is not available.");
        for (int f = 1; f <= fields.NumberOfFields; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = fields.GetField(f);
            if (field.XSolve == SolveType.Variable)
            {
                ValidateFinite(field.X, $"field {f} X");
                variables.Add(NewVariable(ref varNum, $"Field {f} X", VariableType.FieldX, field.X, fieldNumber: f));
            }
            if (field.YSolve == SolveType.Variable)
            {
                ValidateFinite(field.Y, $"field {f} Y");
                variables.Add(NewVariable(ref varNum, $"Field {f} Y", VariableType.FieldY, field.Y, fieldNumber: f));
            }
        }

        var mce = system.MCE ?? throw new InvalidOperationException("Multi-Configuration Editor is not available.");
        int numConfigs = mce.NumberOfConfigurations;
        if (numConfigs > 1)
        {
            int numOperands = mce.NumberOfOperands;
            for (int row = 1; row <= numOperands; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operand = mce.GetOperandAt(row)
                    ?? throw new InvalidOperationException($"OpticStudio returned no MCE operand at row {row}.");
                string typeName = operand.TypeName;

                for (int configuration = 1; configuration <= numConfigs; configuration++)
                {
                    if ((configuration & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var cell = operand.GetOperandCell(configuration)
                        ?? throw new InvalidOperationException($"MCE row {row}, configuration {configuration} returned no operand cell.");
                    if (!cell.IsActive) continue;
                    if (cell.Solve == SolveType.Variable)
                    {
                        if (cell.DataType != CellDataType.Double)
                            throw new InvalidDataException($"MCE row {row}, configuration {configuration} is variable but uses unsupported data type {cell.DataType}; constrained optimization supports Double configuration variables only.");
                        double value = cell.DoubleValue;
                        ValidateFinite(value, $"MCE row {row} configuration {configuration}");
                        variables.Add(NewVariable(ref varNum,
                            $"MCE Row {row} ({typeName}) Config {configuration}",
                            VariableType.ConfigOperand,
                            value,
                            configOperandRow: row,
                            configColumn: configuration));
                    }
                }
            }
        }

        return variables;
    }

    public List<MaterialInfo> ScanMaterials(IOpticalSystem system, CancellationToken cancellationToken = default)
    {
        if (system == null) throw new ArgumentNullException(nameof(system));
        var materials = new List<MaterialInfo>();
        var lde = system.LDE ?? throw new InvalidOperationException("Lens Data Editor is not available.");
        int numSurfaces = lde.NumberOfSurfaces;
        var substituteCatalogs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < numSurfaces; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ILDERow surface = lde.GetSurfaceAt(i)
                ?? throw new InvalidOperationException($"OpticStudio returned no LDE row for surface {i}.");
            string material = surface.Material;
            if (string.IsNullOrEmpty(material))
                continue;

            var solveData = surface.MaterialCell.GetSolveData()
                ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} did not return material solve data.");
            var info = new MaterialInfo
            {
                SurfaceIndex = i,
                SurfaceNumber = surface.SurfaceNumber,
                Material = material,
                Catalog = surface.MaterialCatalog,
                SolveType = solveData.Type
            };

            if (solveData.Type == SolveType.MaterialSubstitute)
            {
                var sub = solveData._S_MaterialSubstitute
                    ?? throw new InvalidOperationException($"Surface {surface.SurfaceNumber} uses MaterialSubstitute but typed solve data is unavailable.");
                info.SubstituteCatalog = sub.Catalog;
            }

            materials.Add(info);
        }

        foreach (var mat in materials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mat.SolveType != SolveType.MaterialSubstitute || string.IsNullOrWhiteSpace(mat.SubstituteCatalog))
                continue;

            if (substituteCatalogs.TryGetValue(mat.SubstituteCatalog, out var cached))
            {
                mat.SubstituteGlasses = cached;
                continue;
            }

            var catTool = system.Tools.OpenMaterialsCatalog()
                ?? throw new InvalidOperationException("OpticStudio could not open the Materials Catalog tool.");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                catTool.SelectedCatalog = mat.SubstituteCatalog;
                var glasses = catTool.GetAllMaterials()
                    ?? throw new InvalidDataException($"Materials Catalog '{mat.SubstituteCatalog}' returned a null material list.");
                cancellationToken.ThrowIfCancellationRequested();
                if (glasses.Length == 0)
                    throw new InvalidDataException($"Materials Catalog '{mat.SubstituteCatalog}' returned no materials for an active MaterialSubstitute solve.");
                mat.SubstituteGlasses = glasses;
                substituteCatalogs[mat.SubstituteCatalog] = glasses;
            }
            finally
            {
                catTool.Close();
            }
        }

        return materials;
    }

    private static OptVariable NewVariable(
        ref int variableNumber,
        string description,
        VariableType type,
        double value,
        int surfaceNumber = 0,
        int parameterNumber = 0,
        int fieldNumber = 0,
        int configOperandRow = 0,
        int configColumn = 0)
    {
        return new OptVariable
        {
            VariableNumber = variableNumber++,
            Description = description,
            Type = type,
            SurfaceNumber = surfaceNumber,
            ParameterNumber = parameterNumber,
            FieldNumber = fieldNumber,
            ConfigOperandRow = configOperandRow,
            ConfigColumn = configColumn,
            Value = value,
            StartingValue = value
        };
    }

    private static void ValidateFinite(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"Variable scanner found non-finite {label}: {value}.");
    }
}
