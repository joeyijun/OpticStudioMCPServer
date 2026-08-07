using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Editors;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class SetConfigurationOperandValueTool
{
    private readonly IZemaxSession _session;

    public SetConfigurationOperandValueTool(IZemaxSession session) => _session = session;

    public record ConfigurationCellValue(
        int ConfigurationNumber,
        string DataType,
        double? DoubleValue,
        int? IntegerValue,
        string? StringValue,
        string SolveType,
        int? PickupConfig = null,
        int? PickupOperand = null,
        double? ScaleFactor = null,
        double? Offset = null);

    public record SetConfigurationOperandValueResult(
        bool Success,
        string? Error,
        ConfigurationCellValue? NewValue);

    [ZemaxTool(Name = "zemax_set_configuration_operand_value")]
    [Description("Set one MCE cell to a fixed double/integer/string value or to a ConfigPickup solve. Exactly one value mode or pickupConfig must be supplied. Readback preserves the actual MCE cell data type and pickup source.")]
    public async Task<SetConfigurationOperandValueResult> ExecuteAsync(
        [Description("Operand row number (1-indexed)")] int operandRow,
        [Description("Configuration number (1-indexed)")] int configurationNumber,
        [Description("Fixed double value. Use only for a Double MCE cell.")] double? value = null,
        [Description("Fixed integer value. Use only for an Integer MCE cell.")] int? integerValue = null,
        [Description("Fixed string value, including an explicit empty string. Use only for a String MCE cell.")] string? stringValue = null,
        [Description("Set a ConfigPickup solve from this configuration number instead of a fixed value.")] int? pickupConfig = null,
        [Description("Source operand row for ConfigPickup. Defaults to operandRow when omitted.")] int? pickupOperand = null,
        [Description("Scale factor for ConfigPickup. Only valid with pickupConfig.")] double? scaleFactor = null,
        [Description("Offset for ConfigPickup. Only valid with pickupConfig.")] double? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (operandRow < 1) throw new ArgumentOutOfRangeException(nameof(operandRow), "operandRow must be >= 1.");
            if (configurationNumber < 1) throw new ArgumentOutOfRangeException(nameof(configurationNumber), "configurationNumber must be >= 1.");
            ValidateFinite(value, nameof(value));
            ValidateFinite(scaleFactor, nameof(scaleFactor));
            ValidateFinite(offset, nameof(offset));

            int fixedModeCount = (value.HasValue ? 1 : 0) + (integerValue.HasValue ? 1 : 0) + (stringValue is not null ? 1 : 0);
            if (pickupConfig.HasValue)
            {
                if (fixedModeCount != 0)
                    throw new ArgumentException("Fixed value parameters and pickupConfig are mutually exclusive.");
                if (pickupConfig.Value < 1)
                    throw new ArgumentOutOfRangeException(nameof(pickupConfig), "pickupConfig must be >= 1.");
                if (pickupOperand.HasValue && pickupOperand.Value < 1)
                    throw new ArgumentOutOfRangeException(nameof(pickupOperand), "pickupOperand must be >= 1.");
            }
            else
            {
                if (fixedModeCount != 1)
                    throw new ArgumentException("Provide exactly one of value, integerValue, stringValue, or pickupConfig.");
                if (pickupOperand.HasValue || scaleFactor.HasValue || offset.HasValue)
                    throw new ArgumentException("pickupOperand, scaleFactor, and offset are valid only when pickupConfig is provided.");
            }

            var parameters = new Dictionary<string, object?>
            {
                ["operandRow"] = operandRow,
                ["configurationNumber"] = configurationNumber,
                ["value"] = value,
                ["integerValue"] = integerValue,
                ["stringValue"] = stringValue,
                ["pickupConfig"] = pickupConfig,
                ["pickupOperand"] = pickupOperand,
                ["scaleFactor"] = scaleFactor,
                ["offset"] = offset
            };

            return await _session.ExecuteAsync("SetConfigurationOperandValue", parameters, system =>
            {
                var mce = system.MCE;
                if (operandRow > mce.NumberOfOperands)
                    throw new ArgumentOutOfRangeException(nameof(operandRow), $"operandRow {operandRow} exceeds the MCE operand count ({mce.NumberOfOperands}).");
                if (configurationNumber > mce.NumberOfConfigurations)
                    throw new ArgumentOutOfRangeException(nameof(configurationNumber), $"configurationNumber {configurationNumber} exceeds the configuration count ({mce.NumberOfConfigurations}).");

                var row = mce.GetOperandAt(operandRow);
                var cell = row.GetOperandCell(configurationNumber);
                if (cell == null || !cell.IsActive)
                    throw new InvalidOperationException($"MCE cell operand {operandRow}, configuration {configurationNumber} is not active.");
                if (cell.IsReadOnly)
                    throw new InvalidOperationException($"MCE cell operand {operandRow}, configuration {configurationNumber} is read-only.");

                if (pickupConfig.HasValue)
                {
                    int sourceOperand = pickupOperand ?? operandRow;
                    if (pickupConfig.Value > mce.NumberOfConfigurations)
                        throw new ArgumentOutOfRangeException(nameof(pickupConfig), $"pickupConfig {pickupConfig.Value} exceeds the configuration count ({mce.NumberOfConfigurations}).");
                    if (sourceOperand > mce.NumberOfOperands)
                        throw new ArgumentOutOfRangeException(nameof(pickupOperand), $"pickupOperand {sourceOperand} exceeds the MCE operand count ({mce.NumberOfOperands}).");
                    if (pickupConfig.Value == configurationNumber && sourceOperand == operandRow)
                        throw new ArgumentException("A ConfigPickup cannot reference the same MCE cell as its own source.");
                    if (!cell.IsSolveTypeSupported(SolveType.ConfigPickup))
                        throw new InvalidOperationException("This MCE cell does not support ConfigPickup solves.");

                    var solveData = cell.CreateSolveType(SolveType.ConfigPickup)
                        ?? throw new InvalidOperationException("OpticStudio could not create a ConfigPickup solve.");
                    var pickup = solveData._S_ConfigPickup
                        ?? throw new InvalidOperationException("OpticStudio did not expose typed ConfigPickup solve data.");
                    pickup.Configuration = pickupConfig.Value;
                    pickup.Operand = sourceOperand;

                    double requestedScale = scaleFactor ?? 1.0;
                    double requestedOffset = offset ?? 0.0;
                    if (pickup.SupportsScale) pickup.ScaleFactor = requestedScale;
                    else if (scaleFactor.HasValue && requestedScale != 1.0)
                        throw new InvalidOperationException("This ConfigPickup solve does not support a scale factor.");
                    if (pickup.SupportsOffset) pickup.Offset = requestedOffset;
                    else if (offset.HasValue && requestedOffset != 0.0)
                        throw new InvalidOperationException("This ConfigPickup solve does not support an offset.");

                    var status = cell.SetSolveData(solveData);
                    if (status != SolveStatus.Success)
                        throw new InvalidOperationException($"OpticStudio rejected the ConfigPickup solve: {status}.");
                }
                else
                {
                    ValidateFixedValueMatchesCellType(cell.DataType, value, integerValue, stringValue);
                    if (!cell.MakeSolveFixed())
                        throw new InvalidOperationException("OpticStudio could not make the MCE cell fixed.");

                    switch (cell.DataType)
                    {
                        case CellDataType.Double:
                            cell.DoubleValue = value!.Value;
                            break;
                        case CellDataType.Integer:
                            cell.IntegerValue = integerValue!.Value;
                            break;
                        case CellDataType.String:
                            cell.Value = stringValue!;
                            break;
                        default:
                            throw new InvalidOperationException($"Unsupported MCE cell data type {cell.DataType}.");
                    }
                }

                return new SetConfigurationOperandValueResult(true, null, ReadCell(configurationNumber, cell));
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SetConfigurationOperandValueResult(false, ex.Message, null);
        }
    }

    private static void ValidateFixedValueMatchesCellType(
        CellDataType dataType,
        double? doubleValue,
        int? integerValue,
        string? stringValue)
    {
        bool matches = dataType switch
        {
            CellDataType.Double => doubleValue.HasValue && !integerValue.HasValue && stringValue is null,
            CellDataType.Integer => integerValue.HasValue && !doubleValue.HasValue && stringValue is null,
            CellDataType.String => stringValue is not null && !doubleValue.HasValue && !integerValue.HasValue,
            _ => false
        };
        if (!matches)
            throw new ArgumentException($"The supplied fixed-value parameter does not match the MCE cell data type {dataType}.");
    }

    private static ConfigurationCellValue ReadCell(int configurationNumber, IEditorCell cell)
    {
        double? doubleValue = null;
        int? integerValue = null;
        string? stringValue = null;
        switch (cell.DataType)
        {
            case CellDataType.Double: doubleValue = cell.DoubleValue; break;
            case CellDataType.Integer: integerValue = cell.IntegerValue; break;
            case CellDataType.String: stringValue = cell.Value; break;
            default: throw new InvalidOperationException($"Unsupported MCE cell data type: {cell.DataType}.");
        }

        var solveData = cell.GetSolveData();
        int? pickupConfig = null;
        int? pickupOperand = null;
        double? scale = null;
        double? offset = null;
        if (solveData.Type == SolveType.ConfigPickup)
        {
            var pickup = solveData._S_ConfigPickup
                ?? throw new InvalidOperationException("OpticStudio did not expose typed ConfigPickup solve data during readback.");
            pickupConfig = pickup.Configuration;
            pickupOperand = pickup.Operand;
            if (pickup.SupportsScale) scale = pickup.ScaleFactor;
            if (pickup.SupportsOffset) offset = pickup.Offset;
        }

        return new ConfigurationCellValue(
            configurationNumber,
            cell.DataType.ToString(),
            doubleValue,
            integerValue,
            stringValue,
            solveData.Type.ToString(),
            pickupConfig,
            pickupOperand,
            scale,
            offset);
    }

    private static void ValidateFinite(double? input, string name)
    {
        if (input.HasValue && (double.IsNaN(input.Value) || double.IsInfinity(input.Value)))
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite.");
    }
}
