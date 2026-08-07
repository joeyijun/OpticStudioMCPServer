using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZOSAPI.Editors;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class GetConfigurationOperandsTool
{
    private readonly IZemaxSession _session;

    public GetConfigurationOperandsTool(IZemaxSession session) => _session = session;

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

    public record ConfigurationOperandInfo(
        int OperandNumber,
        string OperandType,
        int Param1,
        int Param2,
        int Param3,
        List<ConfigurationCellValue> Values);

    public record GetConfigurationOperandsResult(
        bool Success,
        string? Error,
        int NumberOfOperands,
        int NumberOfConfigurations,
        List<ConfigurationOperandInfo> Operands);

    [ZemaxTool(Name = "zemax_get_configuration_operands")]
    [Description("Get MCE operands and typed cell values across configurations, including ConfigPickup source configuration/operand and supported scale/offset. Invalid row ranges are rejected instead of silently clamped.")]
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

            var parameters = new Dictionary<string, object?> { ["startRow"] = startRow, ["endRow"] = endRow };
            return await _session.ExecuteAsync("GetConfigurationOperands", parameters, system =>
            {
                var mce = system.MCE;
                int numOperands = mce.NumberOfOperands;
                int numConfigs = mce.NumberOfConfigurations;
                var operands = new List<ConfigurationOperandInfo>();

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

                    var values = new List<ConfigurationCellValue>();
                    for (int configNum = 1; configNum <= numConfigs; configNum++)
                    {
                        if ((configNum & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                        var cell = row.GetOperandCell(configNum);
                        if (cell == null || !cell.IsActive)
                            throw new InvalidOperationException($"MCE cell row {rowNum}, configuration {configNum} is not active.");
                        values.Add(ReadCell(configNum, cell));
                    }

                    operands.Add(new ConfigurationOperandInfo(
                        rowNum,
                        row.Type.ToString(),
                        row.Param1,
                        row.Param2,
                        row.Param3,
                        values));
                }

                return new GetConfigurationOperandsResult(true, null, numOperands, numConfigs, operands);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new GetConfigurationOperandsResult(false, ex.Message, 0, 0, new List<ConfigurationOperandInfo>());
        }
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
}
