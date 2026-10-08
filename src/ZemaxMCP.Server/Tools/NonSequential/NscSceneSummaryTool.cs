using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.NonSequential;

[ZemaxToolType]
public sealed class NscSceneSummaryTool
{
    private const int MaximumObjects = 5000;
    private readonly IZemaxSession _session;

    public NscSceneSummaryTool(IZemaxSession session) => _session = session;

    public record Result(
        bool Success,
        string? Error,
        int NumberOfObjects,
        int ActiveObjects,
        int DetectorObjects,
        int ReferencedObjects,
        int NestedObjects,
        IReadOnlyDictionary<string, int> TypeCounts,
        IReadOnlyList<string> Materials,
        IReadOnlyList<string> Warnings);

    [ZemaxTool(Name = "zemax_nsc_scene_summary")]
    [Description("Summarize a non-sequential scene for AI diagnostics: object/type counts, active and detector counts, reference/nesting usage, materials, and structural warnings. Read-only.")]
    public async Task<Result> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("NscSceneSummary", null, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (system.Mode != SystemType.NonSequential)
                    return Failure("The current system is sequential. Open a non-sequential system before using this tool.");

                var nce = system.NCE ?? throw new InvalidOperationException("Non-Sequential Component Editor is not available.");
                var count = nce.NumberOfObjects;
                if (count < 0 || count > MaximumObjects)
                    throw new InvalidDataException($"NSC editor reported unsupported object count {count}; maximum inspected count is {MaximumObjects}.");

                var active = 0;
                var detectors = 0;
                var referenced = 0;
                var nested = 0;
                var types = new Dictionary<string, int>(StringComparer.Ordinal);
                var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var warnings = new List<string>();

                for (var number = 1; number <= count; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = nce.GetObjectAt(number)
                        ?? throw new InvalidOperationException($"OpticStudio returned no NCE row for object {number}.");

                    if (row.IsActive) active++;
                    if (NscObjectClassification.IsDetector(row)) detectors++;
                    if (row.RefObject != 0) referenced++;
                    if (row.InsideOf != 0) nested++;

                    var type = string.IsNullOrWhiteSpace(row.TypeName) ? "<unknown>" : row.TypeName;
                    types[type] = types.TryGetValue(type, out var existing) ? existing + 1 : 1;
                    if (!string.IsNullOrWhiteSpace(row.Material)) materials.Add(row.Material);

                    ValidateFinite(row.XPosition, number, nameof(row.XPosition));
                    ValidateFinite(row.YPosition, number, nameof(row.YPosition));
                    ValidateFinite(row.ZPosition, number, nameof(row.ZPosition));
                    ValidateFinite(row.TiltAboutX, number, nameof(row.TiltAboutX));
                    ValidateFinite(row.TiltAboutY, number, nameof(row.TiltAboutY));
                    ValidateFinite(row.TiltAboutZ, number, nameof(row.TiltAboutZ));

                    if (row.RefObject < 0 || row.RefObject > count)
                        warnings.Add($"Object {number} references invalid RefObject {row.RefObject}.");
                    if (row.InsideOf < 0 || row.InsideOf > count)
                        warnings.Add($"Object {number} has invalid InsideOf {row.InsideOf}.");
                    if (row.RefObject == number)
                        warnings.Add($"Object {number} references itself.");
                    if (row.InsideOf == number)
                        warnings.Add($"Object {number} is nested inside itself.");
                }

                return new Result(
                    true, null, count, active, detectors, referenced, nested,
                    new Dictionary<string, int>(types, StringComparer.Ordinal),
                    materials.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
                    warnings);
            }, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Failure(ex.Message); }
    }

    private static Result Failure(string error) =>
        new(false, error, 0, 0, 0, 0, 0,
            new Dictionary<string, int>(StringComparer.Ordinal),
            Array.Empty<string>(), Array.Empty<string>());

    private static void ValidateFinite(double value, int objectNumber, string property)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"NSC object {objectNumber} returned non-finite {property}={value}.");
    }
}
