using System.ComponentModel;
using System.Text.Json;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class SetVariableConstraintsTool
{
    private readonly IZemaxSession _session;
    private readonly ConstraintStore _constraintStore;

    public SetVariableConstraintsTool(IZemaxSession session, ConstraintStore constraintStore)
    {
        _session = session;
        _constraintStore = constraintStore;
    }

    public record ConstraintInput(
        int VariableNumber,
        string Constraint,
        double? Min,
        double? Max
    );

    public record SetConstraintsResult(
        bool Success,
        string? Error,
        int ConstraintsSet
    );

    [ZemaxTool(Name = "zemax_set_variable_constraints")]
    [Description("Set min/max bounds on one or more variables for constrained optimization. Variables are identified by variable number from zemax_get_variables. The complete batch is validated before any stored constraint changes are committed.")]
    public async Task<SetConstraintsResult> ExecuteAsync(
        [Description("JSON array of constraints: [{\"VariableNumber\": 1, \"Constraint\": \"MinAndMax\", \"Min\": -10, \"Max\": 10}]. Constraint values: Unconstrained, MinAndMax, MinOnly, MaxOnly")]
        string constraints,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(constraints))
                return new SetConstraintsResult(false, "No constraints provided.", 0);

            var inputs = JsonSerializer.Deserialize<ConstraintInput[]>(constraints, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (inputs == null || inputs.Length == 0)
                return new SetConstraintsResult(false, "No constraints provided.", 0);

            if (inputs.GroupBy(input => input.VariableNumber).Any(group => group.Count() > 1))
                return new SetConstraintsResult(false, "Each variable number may appear at most once in a constraint batch.", 0);

            return await _session.ExecuteAsync("SetVariableConstraints", null, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var scanner = new VariableScanner();
                var variables = scanner.ScanVariables(system);
                var varLookup = variables.ToDictionary(v => v.VariableNumber);
                var staged = _constraintStore.GetAll();

                foreach (var input in inputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!varLookup.TryGetValue(input.VariableNumber, out var variable))
                        throw new ArgumentException($"Variable number {input.VariableNumber} not found. Run zemax_get_variables to see available variables.");

                    var constraintText = input.Constraint?.Trim();
                    if (string.IsNullOrWhiteSpace(constraintText) ||
                        !Enum.TryParse<ConstraintType>(constraintText, ignoreCase: true, out var constraintType) ||
                        !Enum.IsDefined(typeof(ConstraintType), constraintType))
                    {
                        throw new ArgumentException($"Invalid constraint type '{input.Constraint}'. Valid values: Unconstrained, MinAndMax, MinOnly, MaxOnly.");
                    }

                    ValidateConstraintInput(input, constraintType);
                    var min = input.Min ?? 0.0;
                    var max = input.Max ?? 0.0;
                    staged[variable.CompositeKey] = new ConstraintStore.StoredConstraint(constraintType, min, max);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var previous = _constraintStore.GetAll();
                try
                {
                    _constraintStore.ReplaceAll(staged);
                    var filePath = system.SystemFile;
                    if (!string.IsNullOrWhiteSpace(filePath))
                        _constraintStore.SaveToFile(filePath);
                }
                catch
                {
                    _constraintStore.ReplaceAll(previous);
                    throw;
                }

                return new SetConstraintsResult(
                    Success: true,
                    Error: null,
                    ConstraintsSet: inputs.Length
                );
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SetConstraintsResult(false, ex.Message, 0);
        }
    }

    private static void ValidateConstraintInput(ConstraintInput input, ConstraintType constraintType)
    {
        if (input.VariableNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(input.VariableNumber), "Variable number must be at least 1.");

        ValidateFinite(input.Min, $"Variable {input.VariableNumber} Min");
        ValidateFinite(input.Max, $"Variable {input.VariableNumber} Max");

        switch (constraintType)
        {
            case ConstraintType.Unconstrained:
                if (input.Min != null || input.Max != null)
                    throw new ArgumentException($"Variable {input.VariableNumber}: Unconstrained must not include Min or Max values.");
                break;

            case ConstraintType.MinAndMax:
                if (input.Min == null || input.Max == null)
                    throw new ArgumentException($"Variable {input.VariableNumber}: MinAndMax requires both Min and Max values.");
                if (input.Min.Value >= input.Max.Value)
                    throw new ArgumentException($"Variable {input.VariableNumber}: Min ({input.Min.Value}) must be less than Max ({input.Max.Value}).");
                break;

            case ConstraintType.MinOnly:
                if (input.Min == null)
                    throw new ArgumentException($"Variable {input.VariableNumber}: MinOnly requires a Min value.");
                if (input.Max != null)
                    throw new ArgumentException($"Variable {input.VariableNumber}: MinOnly must not include a Max value because it would be ignored.");
                break;

            case ConstraintType.MaxOnly:
                if (input.Max == null)
                    throw new ArgumentException($"Variable {input.VariableNumber}: MaxOnly requires a Max value.");
                if (input.Min != null)
                    throw new ArgumentException($"Variable {input.VariableNumber}: MaxOnly must not include a Min value because it would be ignored.");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(constraintType), constraintType, "Unknown constraint type.");
        }
    }

    private static void ValidateFinite(double? value, string name)
    {
        if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
            throw new ArgumentOutOfRangeException(name, "Constraint bounds must be finite numbers.");
    }
}
