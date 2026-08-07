using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class RemoveOperandTool
{
    private readonly IZemaxSession _session;

    public RemoveOperandTool(IZemaxSession session) => _session = session;

    public record RemoveOperandResult(
        bool Success,
        string? Error,
        int RemovedRow,
        int RemainingOperands);

    [ZemaxTool(Name = "zemax_remove_operand")]
    [Description("Remove one Merit Function Editor operand and verify OpticStudio accepted the deletion and the operand count changed exactly once.")]
    public async Task<RemoveOperandResult> ExecuteAsync(
        [Description("Row number to remove (1-indexed)")] int row,
        CancellationToken cancellationToken = default)
    {
        if (row < 1)
            return new RemoveOperandResult(false, "Row number must be at least 1.", row, 0);

        try
        {
            return await _session.ExecuteAsync("RemoveOperand",
                new Dictionary<string, object?> { ["row"] = row },
                system =>
                {
                    var mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
                    int before = mfe.NumberOfOperands;
                    if (row > before)
                        throw new ArgumentOutOfRangeException(nameof(row), $"Row {row} does not exist. MFE has {before} operands.");
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!mfe.RemoveOperandAt(row))
                        throw new InvalidOperationException($"OpticStudio rejected deletion of Merit Function operand {row}.");
                    if (mfe.NumberOfOperands != before - 1)
                        throw new InvalidOperationException($"MFE operand count is {mfe.NumberOfOperands} after deleting row {row}; expected {before - 1}.");

                    return new RemoveOperandResult(true, null, row, mfe.NumberOfOperands);
                }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RemoveOperandResult(false, ex.Message, row, 0);
        }
    }
}
