using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class GetVariablesTool
{
    private readonly IZemaxSession _session;
    private readonly ConstraintStore _constraintStore;

    public GetVariablesTool(IZemaxSession session, ConstraintStore constraintStore)
    {
        _session = session;
        _constraintStore = constraintStore;
    }

    public record VariableInfo(
        int VariableNumber,
        string Description,
        string Type,
        int SurfaceNumber,
        int ParameterNumber,
        int FieldNumber,
        int ConfigOperandRow,
        int ConfigColumn,
        double Value,
        string Constraint,
        double? Min,
        double? Max
    );

    public record GetVariablesResult(
        bool Success,
        string? Error,
        int VariableCount,
        VariableInfo[] Variables
    );

    [ZemaxTool(Name = "zemax_get_variables")]
    [Description("Scan the current optical system for variable solves and return finite current values with stored constraint settings. MCE variables are addressed by configuration number through GetOperandCell.")]
    public async Task<GetVariablesResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _session.ExecuteAsync("GetVariables", null, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scanner = new VariableScanner();
                var variables = scanner.ScanVariables(system, cancellationToken);
                _constraintStore.ApplyConstraints(variables);

                var infos = new VariableInfo[variables.Count];
                for (int i = 0; i < variables.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var variable = variables[i];
                    if (double.IsNaN(variable.Value) || double.IsInfinity(variable.Value))
                        throw new InvalidDataException($"Variable {variable.VariableNumber} ({variable.Description}) returned non-finite value {variable.Value}.");

                    double? min = variable.Constraint is ConstraintType.MinAndMax or ConstraintType.MinOnly ? variable.Min : null;
                    double? max = variable.Constraint is ConstraintType.MinAndMax or ConstraintType.MaxOnly ? variable.Max : null;
                    if (min.HasValue && (double.IsNaN(min.Value) || double.IsInfinity(min.Value)))
                        throw new InvalidDataException($"Variable {variable.VariableNumber} has non-finite minimum constraint {min.Value}.");
                    if (max.HasValue && (double.IsNaN(max.Value) || double.IsInfinity(max.Value)))
                        throw new InvalidDataException($"Variable {variable.VariableNumber} has non-finite maximum constraint {max.Value}.");

                    infos[i] = new VariableInfo(
                        variable.VariableNumber,
                        variable.Description,
                        variable.Type.ToString(),
                        variable.SurfaceNumber,
                        variable.ParameterNumber,
                        variable.FieldNumber,
                        variable.ConfigOperandRow,
                        variable.ConfigColumn,
                        variable.Value,
                        variable.Constraint.ToString(),
                        min,
                        max);
                }

                return new GetVariablesResult(true, null, infos.Length, infos);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GetVariablesResult(false, ex.Message, 0, Array.Empty<VariableInfo>());
        }
    }
}
