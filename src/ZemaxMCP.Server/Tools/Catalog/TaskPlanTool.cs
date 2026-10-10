using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.ToolManifest;
using ZemaxMCP.Toolsets;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>
/// Examines the active optical model and returns a small, read-only planning
/// contract. This intentionally does not invoke the recommended steps.
/// </summary>
[ZemaxToolType]
public sealed class TaskPlanTool
{
    private readonly IZemaxSession _session;
    public TaskPlanTool(IZemaxSession session) => _session = session;

    public sealed record PlanStep(int Order, string Tool, string Purpose,
        bool VisibleInHostProfile, bool ModeApplicable, bool RequiresConfirmation);
    public sealed record PlanResult(bool Success, string? Error, string Task,
        string SystemMode, string? SystemFile, int? Surfaces, int? NscObjects,
        int? Wavelengths, int? Fields, bool ModeCompatible,
        IReadOnlyList<string> PreflightWarnings,
        IReadOnlyList<PlanStep> Steps, string ExecutionPolicy);

    private static readonly IReadOnlyDictionary<string, string[]> Purposes =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["clipping"] = new[] { "Summarize active model, units and key optical elements", "Confirm active system", "Read target surface", "Inspect physical aperture",
                "Return a ranked, cross-checked clipping diagnosis", "Trace failing rays",
                "Calculate footprint envelope", "Cross-check geometric pupil fraction" },
            ["imaging"] = new[] { "Summarize active model, units and key optical elements", "Read optical system", "Read field sampling", "Measure RMS spot",
                "Inspect PSF", "Inspect MTF" },
            ["straylight"] = new[] { "Summarize active model, units and key optical elements", "Inspect NSC system settings", "Summarize scene",
                "Inspect NSC object IDs", "Trace rays (explicitly confirm detector clearing)",
                "Read native detector ROI", "Compare non-additive detector flux against an explicit source denominator" },
            ["energy"] = new[] { "Summarize active model, units and key optical elements", "Inspect source wavelengths", "Inspect fields",
                "Trace sequential energy path", "Cross-check pupil loss", "Inspect footprint",
                "Check non-sequential objects", "Read detector pixels", "Compute detector/source ratios" },
            ["optimize"] = new[] { "Summarize active model, units and key optical elements", "Read baseline", "Read merit function",
                "Inspect variables", "Locate snapshot", "Run optimization only after approval" },
            ["tolerance"] = new[] { "Summarize active model, units and key optical elements", "Inspect tolerance criterion", "Inspect tolerance operands",
                "Run bounded tolerancing after approval" },
            ["safe-edit"] = new[] { "Summarize active model, units and key optical elements", "Read baseline", "Inspect target surface", "Check existing snapshots",
                "Edit only after approval", "Read back changed surface",
                "Check snapshot reference", "Review diff", "Save only after approval" }
        };

    [ZemaxTool(Name = "zemax_task_plan")]
    [Description("Inspect the actual active sequential/NSC model and produce an ordered, authorization-aware optical engineering plan (clipping, imaging, straylight, energy, optimize, tolerance, safe-edit). NO edits, traces or optimization are performed. Check actual tools/list before executing steps; per-credential permissions may further restrict the Host profile.")]
    public async Task<PlanResult> ExecuteAsync(
        [Description("Engineering task: clipping, imaging, straylight, energy, optimize, tolerance or safe-edit.")] string task,
        CancellationToken cancellationToken = default)
    {
        var normalized = task?.Trim().ToLowerInvariant() ?? "";
        if (!Purposes.ContainsKey(normalized))
            return new PlanResult(false, "Unknown engineering task. Choose clipping, imaging, straylight, energy, optimize, tolerance or safe-edit.",
                normalized, "unknown", null, null, null, null, null, false,
                Array.Empty<string>(), Array.Empty<PlanStep>(), "No operation was run.");

        try
        {
            // Unlike the static catalog, this reads only active-model metadata
            // through the serialized ZOS-API dispatcher.
            return await _session.ExecuteAsync("TaskPlan",
                new Dictionary<string, object?> { ["task"] = normalized }, system =>
                {
                    var isSequential = system.Mode == SystemType.Sequential;
                    var isNsc = system.Mode == SystemType.NonSequential;
                    var profile = ToolsetCatalog.NormalizeProfile(
                        Environment.GetEnvironmentVariable("ZEMAX_MCP_TOOLSET"));
                    var readOnly = string.Equals(
                        Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY"),
                        "1", StringComparison.Ordinal);
                    var playbook = ToolCatalog.GetPlaybooks(normalized,
                        name => StaticToolManifest.IsAllowed(profile, name, readOnly))[0];
                    var warnings = new List<string>();
                    var compatible = normalized switch
                    {
                        "straylight" => isNsc,
                        "energy" => isSequential || isNsc,
                        _ => isSequential
                    };
                    if (!compatible)
                        warnings.Add("Selected workflow requires a different optical-system mode. No automatic mode conversion or model mutation is permitted.");
                    if (isSequential && system.LDE.NumberOfSurfaces <= 2)
                        warnings.Add("Sequential model has no meaningful intermediate optical surfaces yet.");
                    if (isNsc && system.NCE.NumberOfObjects == 0)
                        warnings.Add("NSC system contains no objects; configure source, optics and detector before tracing.");
                    if (normalized is "clipping" or "energy")
                        warnings.Add("Aperture and ray-intensity fractions are geometric or relative; do not infer absolute watts without source normalization.");
                    if (normalized is "safe-edit" or "optimize" or "tolerance" or "straylight")
                        warnings.Add("Review the active file, input parameters and snapshot/clearing behavior before a high-impact step.");
                    warnings.Add("Available steps reflect the configured Host toolset/read-only mode; per-token permissions and current lease can be stricter. Query tools/list.");

                    var definitions = ToolCatalog.GetPlaybooks(normalized, _ => true)[0];
                    var steps = definitions.AvailableSteps.Select((name, index) =>
                    {
                        var domain = ToolsetCatalog.GetDomain(name).Id;
                        var applicable = compatible && (normalized != "energy" ||
                            (isNsc
                                ? domain is "non-sequential" or "system" or "administration"
                                : domain != "non-sequential"));
                        return new PlanStep(index + 1, name,
                            index < Purposes[normalized].Length ? Purposes[normalized][index] : "Inspect or verify optical result",
                            playbook.AvailableSteps.Contains(name), applicable,
                            StaticToolManifest.GetRequired(name).Impact != "ReadOnly");
                    }).ToArray();
                    return new PlanResult(true, null, normalized,
                        isSequential ? "Sequential" : isNsc ? "NonSequential" : system.Mode.ToString(),
                        system.SystemFile, isSequential ? system.LDE.NumberOfSurfaces : null,
                        isNsc ? system.NCE.NumberOfObjects : null,
                        system.SystemData.Wavelengths.NumberOfWavelengths,
                        isSequential ? system.SystemData.Fields.NumberOfFields : null,
                        compatible, warnings, steps,
                        "PLAN ONLY. Do not automatically execute mutation, save, trace clearing or optimization. Verify input IDs/units and obtain approval before side effects.");
                }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PlanResult(false, ex.Message, normalized, "unavailable",
                null, null, null, null, null, false,
                new[] { "Resolve connection/model prerequisites before executing the workflow." },
                Array.Empty<PlanStep>(), "No workflow steps were executed.");
        }
    }
}
