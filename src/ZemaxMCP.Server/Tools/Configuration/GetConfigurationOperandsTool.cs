using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZOSAPI.Editors;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class GetConfigurationOperandsTool
{
    private readonly IZemaxSession _session;

    public GetConfigurationOperandsTool(IZemaxSession session) => _session = session;

    public record GetConfigurationOperandsResult(
        bool Success,
        string? Error,
        int NumberOfOperands,
        int NumberOfConfigurations,
        List<ConfigurationOperand> Operands
    );

    [ZemaxTool(Name = "zemax_get_configuration_operands")]
    [Description("Get MCE operands and values across configurations. Invalid requested row ranges are rejected instead of silently clamped.")]
    public async Task<GetConfigurationOperandsResult> ExecuteAsync(
        [Description("Starting operand row (1-indexed, default 1)")] int startRow = 1,
        [Description("Ending operand row (0 for all; otherwise 1-indexed and >= startRow)")] int endRow = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (startRow < 1) throw new ArgumentOutOfRangeException(nameof(startRow), "startRow must be >= 1.");
            if (endRow < 0) throw new ArgumentOutOfRangeException(nameof(endRow), "endRow must be 0 (all) or >= 1.");
            if (endRow > 0 && endRow < startRow)
                throw new ArgumentException("endRow must be 0 (all) or greater than or equal to startRow.");

            var parameters = new Dictionary<string, object?>
            {
                ["startRow"] = startRow,
                ["endRow"] = endRow
            };

            return await _session.ExecuteAsync("GetConfigurationOperands", parameters, system =>
            {
                var mce = system.MCE;
                int numOperands = mce.NumberOfOperands;
                int numConfigs = mce.NumberOfConfigurations;
                var operands = new List<ConfigurationOperand>();

                if (numOperands == 0)
                    return new GetConfigurationOperandsResult(true, null, 0, numConfigs, operands);
                if (startRow > numOperands)
                    throw new ArgumentOutOfRangeException(nameof(startRow), $"startRow {startRow} exceeds the MCE operand count ({numOperands}).");
                int end = endRow == 0 ? numOperands : endRow;
                if (end > numOperands)
                    throw new ArgumentOutOfRangeException(nameof(endRow), $"endRow {end} exceeds the MCE operand count ({numOperands}).");

                for (int rowNum = startRow; rowNum <= end; rowNum++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = mce.GetOperandAt(rowNum);
                    if (row == null || !row.IsValidRow)
                        throw new InvalidOperationException($"MCE operand row {rowNum} is not valid.");

                    var values = new List<ConfigurationValue>();
                    for (int configNum = 1; configNum <= numConfigs; configNum++)
                    {
                        if ((configNum & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var cell = row.GetOperandCell(configNum);
                        if (cell == null || !cell.IsActive)
                            throw new InvalidOperationException($"MCE cell row {rowNum}, configuration {configNum} is not active.");
                        if (cell.DataType != CellDataType.Double)
                            throw new InvalidOperationException($"MCE cell row {rowNum}, configuration {configNum} has data type {cell.DataType}; the structured configuration-value contract currently supports numeric cells only.");

                        var solveData = cell.GetSolveData();
                        var configValue = new ConfigurationValue
                        {
                            ConfigurationNumber = configNum,
                            Value = cell.DoubleValue,
                            SolveType = solveData.Type.ToString()
                        };

                        if (solveData.Type == SolveType.ConfigPickup)
                        {
                            var pickup = solveData._S_ConfigPickup
                                ?? throw new InvalidOperationException($"MCE ConfigPickup solve data was unavailable at row {rowNum}, configuration {configNum}.");
                            configValue = configValue with
                            {
                                PickupConfig = pickup.Configuration,
                                ScaleFactor = pickup.SupportsScale ? pickup.ScaleFactor : null,
                                Offset = pickup.SupportsOffset ? pickup.Offset : null
                            };
                        }

                        values.Add(configValue);
                    }

                    operands.Add(new ConfigurationOperand
                    {
                        OperandNumber = rowNum,
                        OperandType = row.Type.ToString(),
                        Param1 = row.Param1,
                        Param2 = row.Param2,
                        Param3 = row.Param3,
                        Values = values
                    });
                }

                return new GetConfigurationOperandsResult(true, null, numOperands, numConfigs, operands);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new GetConfigurationOperandsResult(false, ex.Message, 0, 0, new List<ConfigurationOperand>());
        }
    }
}
