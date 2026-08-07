using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Tolerancing;

[ZemaxToolType]
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
        double? Nominal,
        bool UsesMinimum,
        double? Minimum,
        bool UsesMaximum,
        double? Maximum,
        bool IgnoreDuringTolerancing,
        bool DoNotAdjustDuringInverseTolerancing,
        bool UsesParameter1 = false,
        bool UsesParameter2 = false,
        bool UsesParameter3 = false);

    public record Result(bool Success, string? Error, int NumberOfOperands, IReadOnlyList<ToleranceOperand> Operands);

    [ZemaxTool(Name = "zemax_get_tolerances")]
    [Description("Read the Tolerance Data Editor (TDE) operands for the current system. Unused parameter/bound fields are identified explicitly; any bound marked as used must contain a finite value.")]
    public async Task<Result> ExecuteAsync(
        [Description("First TDE operand row (1-indexed)")] int startRow = 1,
        [Description("Maximum number of operands to return (1-250)")] int maxOperands = 100,
        CancellationToken cancellationToken = default)
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
                cancellationToken.ThrowIfCancellationRequested();
                var tde = system.TDE ?? throw new InvalidOperationException("Tolerance Data Editor is not available.");
                var numberOfOperands = tde.NumberOfOperands;
                if (numberOfOperands <= 0)
                    return new Result(true, null, 0, Array.Empty<ToleranceOperand>());
                if (startRow > numberOfOperands)
                    return new Result(false, $"startRow {startRow} exceeds the {numberOfOperands} TDE operands in the current system.", numberOfOperands, Array.Empty<ToleranceOperand>());

                var lastRowLong = Math.Min((long)numberOfOperands, (long)startRow + maxOperands - 1L);
                var lastRow = checked((int)lastRowLong);
                var operands = new List<ToleranceOperand>(lastRow - startRow + 1);

                for (var rowNumber = startRow; rowNumber <= lastRow; rowNumber++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = tde.GetOperandAt(rowNumber)
                        ?? throw new InvalidOperationException($"OpticStudio returned no TDE row for operand {rowNumber}.");

                    var nominal = ReadUsedFinite(row.IsNominalUsed, row.Nominal, rowNumber, "Nominal");
                    var minimum = ReadUsedFinite(row.IsMinUsed, row.Min, rowNumber, "Min");
                    var maximum = ReadUsedFinite(row.IsMaxUsed, row.Max, rowNumber, "Max");
                    if (row.IsMinUsed && row.IsMaxUsed && minimum!.Value > maximum!.Value)
                        throw new InvalidDataException($"TDE operand {rowNumber} reports Min {minimum.Value} greater than Max {maximum.Value}.");

                    operands.Add(new ToleranceOperand(
                        row.OperandNumber,
                        row.TypeName,
                        row.Comment,
                        row.IsActive,
                        row.Param1,
                        row.Param2,
                        row.Param3,
                        row.IsNominalUsed,
                        nominal,
                        row.IsMinUsed,
                        minimum,
                        row.IsMaxUsed,
                        maximum,
                        row.IgnoreThisOperandDuringTolerancing,
                        row.DoNotAdjustDuringInverseTolerancing,
                        row.IsParam1Used,
                        row.IsParam2Used,
                        row.IsParam3Used));
                }

                return new Result(true, null, numberOfOperands, operands);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, 0, Array.Empty<ToleranceOperand>());
        }
    }

    private static double? ReadUsedFinite(bool used, double value, int rowNumber, string fieldName)
    {
        if (!used)
            return null;
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"TDE operand {rowNumber} marks {fieldName} as used but returned non-finite value {value}.");
        return value;
    }
}
