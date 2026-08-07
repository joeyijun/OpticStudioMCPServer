using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;
using ZOSAPI.Editors;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class SetConfigurationOperandValueTool
{
    private readonly IZemaxSession _session;

    public SetConfigurationOperandValueTool(IZemaxSession session) => _session = session;

    public record SetConfigurationOperandValueResult(
        bool Success,
        string? Error,
        ConfigurationValue? NewValue
    );

    [ZemaxTool(Name = "zemax_set_configuration_operand_value")]
    [Description("Set one MCE operand/configuration cell to either a fixed numeric value or a same-operand ConfigPickup solve. Fixed and pickup modes are mutually exclusive.")]
    public async Task<SetConfigurationOperandValueResult> ExecuteAsync(
        [Description("Operand row number (1-indexed)")] int operandRow,
        [Description("Configuration number (1-indexed)")] int configurationNumber,
        [Description("Fixed value to set. Supply this OR pickupConfig, not both.")] double? value = null,
        [Description("Set a ConfigPickup solve from this configuration number on the same operand row.")] int? pickupConfig = null,
        [Description("Scale factor for ConfigPickup. Only valid with pickupConfig.")] double? scaleFactor = null,
        [Description("Offset for ConfigPickup. Only valid with pickupConfig.")] double? offset = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (operandRow < 1) throw new ArgumentOutOfRangeException(nameof(operandRow), "operandRow must be >= 1.");
            if (configurationNumber < 1) throw new ArgumentOutOfRangeException(nameof(configurationNumber), "configurationNumber must be >= 1.");
            if (value.HasValue == pickupConfig.HasValue)
                throw new ArgumentException("Provide exactly one of 'value' or 'pickupConfig'.");
            if (!pickupConfig.HasValue && (scaleFactor.HasValue || offset.HasValue))
                throw new ArgumentException("scaleFactor and offset are valid only when pickupConfig is provided.");
            ValidateFinite(value, nameof(value));
            ValidateFinite(scaleFactor, nameof(scaleFactor));
            ValidateFinite(offset, nameof(offset));
            if (pickupConfig.HasValue && pickupConfig.Value < 1)
                throw new ArgumentOutOfRangeException(nameof(pickupConfig), "pickupConfig must be >= 1.");

            var parameters = new Dictionary<string, object?>
            {
                ["operandRow"] = operandRow,
                ["configurationNumber"] = configurationNumber,
                ["value"] = value,
                ["pickupConfig"] = pickupConfig,
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
                if (pickupConfig.HasValue && pickupConfig.Value > mce.NumberOfConfigurations)
                    throw new ArgumentOutOfRangeException(nameof(pickupConfig), $"pickupConfig {pickupConfig.Value} exceeds the configuration count ({mce.NumberOfConfigurations}).");

                var row = mce.GetOperandAt(operandRow);
                var cell = row.GetOperandCell(configurationNumber);
                if (cell == null || !cell.IsActive)
                    throw new InvalidOperationException($"MCE cell operand {operandRow}, configuration {configurationNumber} is not active.");
                if (cell.IsReadOnly)
                    throw new InvalidOperationException($"MCE cell operand {operandRow}, configuration {configurationNumber} is read-only.");

                if (pickupConfig.HasValue)
                {
                    if (!cell.IsSolveTypeSupported(SolveType.ConfigPickup))
                        throw new InvalidOperationException("This MCE cell does not support ConfigPickup solves.");

                    var solveData = cell.CreateSolveType(SolveType.ConfigPickup)
                        ?? throw new InvalidOperationException("OpticStudio could not create a ConfigPickup solve.");
                    var pickup = solveData._S_ConfigPickup
                        ?? throw new InvalidOperationException("OpticStudio did not expose typed ConfigPickup solve data.");
                    pickup.Configuration = pickupConfig.Value;
                    pickup.Operand = operandRow;

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
                    if (!cell.MakeSolveFixed())
                        throw new InvalidOperationException("OpticStudio could not make the MCE cell fixed.");
                    cell.DoubleValue = value!.Value;
                }

                var updatedSolve = cell.GetSolveData();
                var newValue = new ConfigurationValue
                {
                    ConfigurationNumber = configurationNumber,
                    Value = cell.DoubleValue,
                    SolveType = updatedSolve.Type.ToString()
                };
                if (updatedSolve.Type == SolveType.ConfigPickup)
                {
                    var pickup = updatedSolve._S_ConfigPickup;
                    newValue = newValue with
                    {
                        PickupConfig = pickup.Configuration,
                        ScaleFactor = pickup.SupportsScale ? pickup.ScaleFactor : null,
                        Offset = pickup.SupportsOffset ? pickup.Offset : null
                    };
                }

                return new SetConfigurationOperandValueResult(true, null, newValue);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SetConfigurationOperandValueResult(false, ex.Message, null);
        }
    }

    private static void ValidateFinite(double? value, string name)
    {
        if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite.");
    }
}
