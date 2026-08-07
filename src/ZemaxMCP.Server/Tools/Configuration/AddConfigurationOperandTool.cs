using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Editors.MCE;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class AddConfigurationOperandTool
{
    private readonly IZemaxSession _session;

    public AddConfigurationOperandTool(IZemaxSession session) => _session = session;

    public record AddConfigurationOperandResult(
        bool Success,
        string? Error,
        int Row,
        string OperandType,
        int NumberOfOperands
    );

    [ZemaxTool(Name = "zemax_add_configuration_operand")]
    [Description("Add a configuration operand to the Multi-Configuration Editor. Operand type is validated before the editor is modified; a failed type/parameter application is rolled back.")]
    public async Task<AddConfigurationOperandResult> ExecuteAsync(
        [Description("Named MCE operand type (for example THIC, CRVT, CONI, PRAM, MOFF). Numeric enum values are not accepted.")] string operandType,
        [Description("Operand position to insert at (1..NumberOfOperands+1), or 0 to append.")] int insertAt = 0,
        [Description("Parameter 1; ignored when zero and the selected operand does not expose Param1.")] int param1 = 0,
        [Description("Parameter 2; ignored when zero and the selected operand does not expose Param2.")] int param2 = 0,
        [Description("Parameter 3; ignored when zero and the selected operand does not expose Param3.")] int param3 = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(operandType))
                throw new ArgumentException("operandType is required.", nameof(operandType));
            if (insertAt < 0)
                throw new ArgumentOutOfRangeException(nameof(insertAt), "insertAt must be 0 (append) or a positive operand position.");

            var enumName = Enum.GetNames(typeof(MultiConfigOperandType))
                .FirstOrDefault(name => name.Equals(operandType.Trim(), StringComparison.OrdinalIgnoreCase));
            if (enumName == null)
                throw new ArgumentException($"Invalid configuration operand type '{operandType}'. Use a named MultiConfigOperandType value.", nameof(operandType));
            var parsedType = (MultiConfigOperandType)Enum.Parse(typeof(MultiConfigOperandType), enumName, ignoreCase: false);

            var parameters = new Dictionary<string, object?>
            {
                ["operandType"] = enumName,
                ["insertAt"] = insertAt,
                ["param1"] = param1,
                ["param2"] = param2,
                ["param3"] = param3
            };

            return await _session.ExecuteAsync("AddConfigurationOperand", parameters, system =>
            {
                var mce = system.MCE;
                if (insertAt > mce.NumberOfOperands + 1)
                    throw new ArgumentOutOfRangeException(nameof(insertAt), $"insertAt must be 0 or in 1..{mce.NumberOfOperands + 1}.");

                IMCERow? row = null;
                try
                {
                    row = insertAt == 0 ? mce.AddOperand() : mce.InsertNewOperandAt(insertAt);
                    if (row == null || !row.IsValidRow)
                        throw new InvalidOperationException("OpticStudio did not create a valid MCE operand row.");
                    if (!row.ChangeType(parsedType))
                        throw new InvalidOperationException($"OpticStudio rejected MCE operand type '{enumName}'.");

                    ApplyParameter(row, 1, param1, row.Param1Enabled, value => row.Param1 = value);
                    ApplyParameter(row, 2, param2, row.Param2Enabled, value => row.Param2 = value);
                    ApplyParameter(row, 3, param3, row.Param3Enabled, value => row.Param3 = value);

                    return new AddConfigurationOperandResult(
                        Success: true,
                        Error: null,
                        Row: row.OperandNumber,
                        OperandType: row.Type.ToString(),
                        NumberOfOperands: mce.NumberOfOperands);
                }
                catch
                {
                    if (row != null && row.IsValidRow)
                    {
                        try { mce.RemoveOperandAt(row.OperandNumber); } catch { }
                    }
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new AddConfigurationOperandResult(false, ex.Message, 0, operandType ?? string.Empty, 0);
        }
    }

    private static void ApplyParameter(IMCERow row, int parameterNumber, int value, bool enabled, Action<int> setter)
    {
        if (enabled)
        {
            setter(value);
            return;
        }
        if (value != 0)
            throw new ArgumentException($"MCE operand type '{row.Type}' does not expose Param{parameterNumber}; non-zero value {value} cannot be applied.");
    }
}
