using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Documentation;
using ZOSAPI.Editors;
using ZOSAPI.Editors.MFE;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class AddOperandTool
{
    private readonly IZemaxSession _session;
    private readonly OperandDatabase _operandDb;

    public AddOperandTool(IZemaxSession session, OperandDatabase operandDb)
    {
        _session = session;
        _operandDb = operandDb;
    }

    public record AddOperandResult(
        bool Success,
        string? Error,
        int Row,
        string OperandType,
        double Value,
        double Target,
        double Weight,
        string? OperandDescription);

    [ZemaxTool(Name = "zemax_add_operand")]
    [Description("Add an optimization operand to the Merit Function Editor. Operand type and numeric inputs are validated before insertion; failed post-insert setup is rolled back or reported explicitly as a partial mutation.")]
    public async Task<AddOperandResult> ExecuteAsync(
        [Description("Named operand type (for example EFFL, MTFT, RSCE). Numeric enum values are not accepted.")] string operandType,
        [Description("Target value; must be finite.")] double target = 0,
        [Description("Weight; must be finite.")] double weight = 1,
        [Description("Row to insert at (1..NumberOfOperands+1), or 0 to append.")] int insertAt = 0,
        [Description("Int1 parameter. Applied to operand cell 2 and must be valid for the selected operand.")] int? int1 = null,
        [Description("Int2 parameter. Applied to operand cell 3 and must be valid for the selected operand.")] int? int2 = null,
        [Description("Optional numeric Data1 parameter for operand cell 4.")] double? data1 = null,
        [Description("Optional numeric Data2 parameter for operand cell 5.")] double? data2 = null,
        [Description("Optional numeric Data3 parameter for operand cell 6.")] double? data3 = null,
        [Description("Optional numeric Data4 parameter for operand cell 7.")] double? data4 = null,
        [Description("Optional numeric Data5 parameter for operand cell 8.")] double? data5 = null,
        [Description("Optional numeric Data6 parameter for operand cell 9.")] double? data6 = null,
        CancellationToken cancellationToken = default)
    {
        string normalizedType = operandType?.Trim() ?? string.Empty;
        var operandDef = _operandDb.GetOperand(normalizedType);
        if (operandDef == null)
        {
            var suggestions = _operandDb.SearchOperands(normalizedType, 3);
            var suggestText = suggestions.Any()
                ? $"Did you mean: {string.Join(", ", suggestions.Select(s => s.Operand.Name))}"
                : "Use zemax_search_operands to find valid operand types.";
            return new AddOperandResult(false, $"Unknown operand type: {normalizedType}. {suggestText}", 0,
                normalizedType, 0, target, weight, null);
        }

        try
        {
            ValidateFinite(target, nameof(target));
            ValidateFinite(weight, nameof(weight));
            ValidateFinite(data1, nameof(data1));
            ValidateFinite(data2, nameof(data2));
            ValidateFinite(data3, nameof(data3));
            ValidateFinite(data4, nameof(data4));
            ValidateFinite(data5, nameof(data5));
            ValidateFinite(data6, nameof(data6));
            if (insertAt < 0)
                throw new ArgumentOutOfRangeException(nameof(insertAt), "insertAt must be 0 (append) or a positive operand position.");

            var enumName = Enum.GetNames(typeof(MeritOperandType))
                .FirstOrDefault(name => name.Equals(normalizedType, StringComparison.OrdinalIgnoreCase));
            if (enumName == null)
                throw new ArgumentException($"Operand database entry '{normalizedType}' is not a named MeritOperandType in this ZOS-API version.", nameof(operandType));
            var parsedType = (MeritOperandType)Enum.Parse(typeof(MeritOperandType), enumName, false);

            var parameters = new Dictionary<string, object?>
            {
                ["operandType"] = enumName,
                ["target"] = target,
                ["weight"] = weight,
                ["insertAt"] = insertAt,
                ["int1"] = int1,
                ["int2"] = int2,
                ["data1"] = data1,
                ["data2"] = data2,
                ["data3"] = data3,
                ["data4"] = data4,
                ["data5"] = data5,
                ["data6"] = data6
            };

            return await _session.ExecuteAsync("AddOperand", parameters, system =>
            {
                var mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
                if (insertAt > mfe.NumberOfOperands + 1)
                    throw new ArgumentOutOfRangeException(nameof(insertAt), $"insertAt must be 0 or in 1..{mfe.NumberOfOperands + 1}.");

                IMFERow? row = null;
                try
                {
                    row = insertAt == 0 ? mfe.AddOperand() : mfe.InsertNewOperandAt(insertAt);
                    if (row == null || !row.IsValidRow || !row.IsActive)
                        throw new InvalidOperationException("OpticStudio did not create a valid active Merit Function operand row.");
                    if (!row.ChangeType(parsedType))
                        throw new InvalidOperationException($"OpticStudio rejected MeritOperandType '{enumName}'.");

                    ApplyIntegerCell(row, 2, int1, nameof(int1));
                    ApplyIntegerCell(row, 3, int2, nameof(int2));
                    ApplyNumericCell(row, 4, data1, nameof(data1));
                    ApplyNumericCell(row, 5, data2, nameof(data2));
                    ApplyNumericCell(row, 6, data3, nameof(data3));
                    ApplyNumericCell(row, 7, data4, nameof(data4));
                    ApplyNumericCell(row, 8, data5, nameof(data5));
                    ApplyNumericCell(row, 9, data6, nameof(data6));

                    row.Target = target;
                    row.Weight = weight;
                    mfe.CalculateMeritFunction();
                    cancellationToken.ThrowIfCancellationRequested();

                    return new AddOperandResult(true, null, row.OperandNumber, row.Type.ToString(), row.Value,
                        row.Target, row.Weight, operandDef.Description);
                }
                catch (Exception original)
                {
                    if (row != null && row.IsValidRow)
                    {
                        bool rolledBack;
                        try { rolledBack = mfe.RemoveOperandAt(row.OperandNumber); }
                        catch (Exception rollbackException)
                        {
                            throw new InvalidOperationException(
                                $"Merit operand setup failed and rollback threw an exception. The inserted row may remain; use the pre-change safety snapshot for recovery. Original error: {original.Message}; rollback error: {rollbackException.Message}",
                                original);
                        }
                        if (!rolledBack)
                        {
                            throw new InvalidOperationException(
                                $"Merit operand setup failed and OpticStudio rejected rollback. The inserted row may remain; use the pre-change safety snapshot for recovery. Original error: {original.Message}",
                                original);
                        }
                    }
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new AddOperandResult(false, ex.Message, 0, normalizedType, 0, target, weight, operandDef.Description);
        }
    }

    private static void ApplyIntegerCell(IMFERow row, int cellIndex, int? requestedValue, string parameterName)
    {
        if (!requestedValue.HasValue) return;
        var cell = GetWritableCell(row, cellIndex, parameterName);
        if (cell.DataType != CellDataType.Integer)
            throw new ArgumentException($"{parameterName} cannot be applied because MeritOperandType '{row.Type}' cell {cellIndex} has data type {cell.DataType}, not Integer.", parameterName);
        cell.IntegerValue = requestedValue.Value;
    }

    private static void ApplyNumericCell(IMFERow row, int cellIndex, double? requestedValue, string parameterName)
    {
        if (!requestedValue.HasValue) return;
        var cell = GetWritableCell(row, cellIndex, parameterName);
        switch (cell.DataType)
        {
            case CellDataType.Double:
                cell.DoubleValue = requestedValue.Value;
                break;
            case CellDataType.Integer:
                double rounded = Math.Round(requestedValue.Value);
                if (Math.Abs(requestedValue.Value - rounded) > 1e-12 || rounded < int.MinValue || rounded > int.MaxValue)
                    throw new ArgumentException($"{parameterName}={requestedValue.Value} must be an integer for MeritOperandType '{row.Type}'.", parameterName);
                cell.IntegerValue = (int)rounded;
                break;
            case CellDataType.String:
                throw new ArgumentException($"{parameterName} cannot set string-valued cell {cellIndex} for MeritOperandType '{row.Type}'.", parameterName);
            default:
                throw new InvalidOperationException($"Unsupported MFE cell data type {cell.DataType} at cell {cellIndex}.");
        }
    }

    private static IEditorCell GetWritableCell(IMFERow row, int cellIndex, string parameterName)
    {
        var cell = row.GetCellAt(cellIndex)
            ?? throw new InvalidOperationException($"Merit operand '{row.Type}' did not expose cell {cellIndex}.");
        if (!cell.IsActive)
            throw new ArgumentException($"Merit operand '{row.Type}' does not expose {parameterName} at cell {cellIndex}.", parameterName);
        if (cell.IsReadOnly)
            throw new InvalidOperationException($"Merit operand '{row.Type}' cell {cellIndex} is read-only.");
        return cell;
    }

    private static void ValidateFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite.");
    }

    private static void ValidateFinite(double? value, string name)
    {
        if (value.HasValue) ValidateFinite(value.Value, name);
    }
}
