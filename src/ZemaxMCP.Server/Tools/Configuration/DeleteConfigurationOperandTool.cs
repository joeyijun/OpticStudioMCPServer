using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class DeleteConfigurationOperandTool
{
    private readonly IZemaxSession _session;

    public DeleteConfigurationOperandTool(IZemaxSession session) => _session = session;

    public record DeleteConfigurationOperandResult(
        bool Success,
        string? Error,
        int DeletedRow,
        int NumberOfOperands
    );

    [ZemaxTool(Name = "zemax_delete_configuration_operand")]
    [Description("Delete one configuration operand from the Multi-Configuration Editor and verify the editor count changed.")]
    public async Task<DeleteConfigurationOperandResult> ExecuteAsync(
        [Description("Operand row number to delete (1-indexed)")] int row,
        CancellationToken cancellationToken = default)
    {
        if (row < 1)
            return new DeleteConfigurationOperandResult(false, "Row number must be at least 1.", 0, 0);

        try
        {
            return await _session.ExecuteAsync("DeleteConfigurationOperand",
                new Dictionary<string, object?> { ["row"] = row },
                system =>
                {
                    var mce = system.MCE;
                    int before = mce.NumberOfOperands;
                    if (row > before)
                        throw new ArgumentOutOfRangeException(nameof(row), $"Row {row} does not exist. MCE has {before} operands.");
                    if (!mce.RemoveOperandAt(row))
                        throw new InvalidOperationException($"OpticStudio rejected deletion of MCE operand {row}.");
                    if (mce.NumberOfOperands != before - 1)
                        throw new InvalidOperationException($"MCE operand count is {mce.NumberOfOperands} after deleting row {row}; expected {before - 1}.");

                    return new DeleteConfigurationOperandResult(true, null, row, mce.NumberOfOperands);
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new DeleteConfigurationOperandResult(false, ex.Message, 0, 0);
        }
    }
}
