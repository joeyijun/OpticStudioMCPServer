using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.Tolerancing;

[ZemaxToolType]
public sealed class ToleranceSummaryTool
{
    private const int MaximumOperands = 2000;
    private readonly IZemaxSession _session;

    public ToleranceSummaryTool(IZemaxSession session) => _session = session;

    public record Result(
        bool Success,
        string? Error,
        int NumberOfOperands,
        int InspectedOperands,
        bool Truncated,
        int ActiveOperands,
        int IgnoredOperands,
        int InverseLockedOperands,
        int BoundedOperands,
        IReadOnlyDictionary<string, int> TypeCounts,
        IReadOnlyList<string> Warnings);

    [ZemaxTool(Name = "zemax_tolerance_summary")]
    [Description("Summarize the Tolerance Data Editor with operand-type counts, active/ignored flags, bound usage, and structural warnings. Read-only; this does not run a tolerance analysis or Monte Carlo.")]
    public async Task<Result> ExecuteAsync(
        [Description("Maximum TDE operands to inspect (1-2000)")] int maxOperands = 500,
        CancellationToken cancellationToken = default)
    {
        if (maxOperands is < 1 or > MaximumOperands)
            return Failure($"maxOperands must be between 1 and {MaximumOperands}.");

        try
        {
            return await _session.ExecuteAsync("ToleranceSummary", new Dictionary<string, object?>
            {
                ["maxOperands"] = maxOperands
            }, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tde = system.TDE ?? throw new InvalidOperationException("Tolerance Data Editor is not available.");
                var total = tde.NumberOfOperands;
                if (total < 0) throw new InvalidDataException($"TDE returned invalid operand count {total}.");

                var inspected = Math.Min(total, maxOperands);
                var active = 0;
                var ignored = 0;
                var inverseLocked = 0;
                var bounded = 0;
                var types = new Dictionary<string, int>(StringComparer.Ordinal);
                var warnings = new List<string>();

                for (var number = 1; number <= inspected; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = tde.GetOperandAt(number)
                        ?? throw new InvalidOperationException($"OpticStudio returned no TDE row for operand {number}.");
                    if (row.IsActive) active++;
                    if (row.IgnoreThisOperandDuringTolerancing) ignored++;
                    if (row.DoNotAdjustDuringInverseTolerancing) inverseLocked++;

                    var type = string.IsNullOrWhiteSpace(row.TypeName) ? "<unknown>" : row.TypeName;
                    types[type] = types.TryGetValue(type, out var existing) ? existing + 1 : 1;

                    if (row.IsMinUsed || row.IsMaxUsed) bounded++;
                    if (row.IsNominalUsed) ValidateFinite(row.Nominal, number, "Nominal");
                    if (row.IsMinUsed) ValidateFinite(row.Min, number, "Min");
                    if (row.IsMaxUsed) ValidateFinite(row.Max, number, "Max");
                    if (row.IsMinUsed && row.IsMaxUsed && row.Min > row.Max)
                        warnings.Add($"Operand {number} ({type}) has Min {row.Min} greater than Max {row.Max}.");
                    if (!row.IsActive && !row.IgnoreThisOperandDuringTolerancing)
                        warnings.Add($"Operand {number} ({type}) is inactive but not explicitly ignored.");
                }

                return new Result(
                    true, null, total, inspected, total > inspected, active, ignored, inverseLocked, bounded,
                    new Dictionary<string, int>(types, StringComparer.Ordinal), warnings);
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure(ex.Message); }
    }

    private static Result Failure(string error) =>
        new(false, error, 0, 0, false, 0, 0, 0, 0,
            new Dictionary<string, int>(StringComparer.Ordinal), Array.Empty<string>());

    private static void ValidateFinite(double value, int row, string field)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"TDE operand {row} marks {field} as used but returned non-finite value {value}.");
    }
}
