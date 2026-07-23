using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Tolerancing;

[McpServerToolType]
public sealed class GetTolerancesTool
{
    private const int MaximumOperandsPerRequest = 250;
    private readonly IZemaxSession _session;

    public GetTolerancesTool(IZemaxSession session) => _session = session;

    public record ToleranceOperand(
        int Number,
        string Type,
        string? Comment,
        bool IsActive,
        int Parameter1,
        int Parameter2,
        int Parameter3,
        bool UsesNominal,
        double Nominal,
        bool UsesMinimum,
        double Minimum,
        bool UsesMaximum,
        double Maximum,
        bool IgnoreDuringTolerancing,
        bool DoNotAdjustDuringInverseTolerancing);

    public record Result(bool Success, string? Error, int NumberOfOperands, IReadOnlyList<ToleranceOperand> Operands);

    [McpServerTool(Name = "zemax_get_tolerances")]
    [Description("Read the Tolerance Data Editor (TDE) operands for the current system. This tool is read-only and works with sequential and non-sequential systems.")]
    public async Task<Result> ExecuteAsync(
        [Description("First TDE operand row (1-indexed)")] int startRow = 1,
        [Description("Maximum number of operands to return (1-250)")] int maxOperands = 100)
    {
        if (startRow < 1)
            return new Result(false, "startRow must be at least 1.", 0, Array.Empty<ToleranceOperand>());
        if (maxOperands is < 1 or > MaximumOperandsPerRequest)
            return new Result(false, $"maxOperands must be between 1 and {MaximumOperandsPerRequest}.", 0, Array.Empty<ToleranceOperand>());

        try
        {
            return await _session.ExecuteAsync("GetTolerances", new Dictionary<string, object?>
            {
                ["startRow"] = startRow,
                ["maxOperands"] = maxOperands
            }, system =>
            {
                var tde = system.TDE;
                var numberOfOperands = tde.NumberOfOperands;
                var lastRow = Math.Min(numberOfOperands, startRow + maxOperands - 1);
                var operands = new List<ToleranceOperand>(Math.Max(0, lastRow - startRow + 1));

                for (var rowNumber = startRow; rowNumber <= lastRow; rowNumber++)
                {
                    var row = tde.GetOperandAt(rowNumber);
                    operands.Add(new ToleranceOperand(
                        row.OperandNumber,
                        row.TypeName,
                        row.Comment,
                        row.IsActive,
                        row.Param1,
                        row.Param2,
                        row.Param3,
                        row.IsNominalUsed,
                        row.Nominal,
                        row.IsMinUsed,
                        row.Min,
                        row.IsMaxUsed,
                        row.Max,
                        row.IgnoreThisOperandDuringTolerancing,
                        row.DoNotAdjustDuringInverseTolerancing));
                }

                return new Result(true, null, numberOfOperands, operands);
            });
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, 0, Array.Empty<ToleranceOperand>());
        }
    }
}
