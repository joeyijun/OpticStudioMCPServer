using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.ToolManifest;
using ZemaxMCP.Toolsets;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>
/// Gives MCP clients a stable, task-oriented map of the installed tool
/// contract. The same static manifest drives MCP tools/list and Worker
/// execution admission, so this catalogue cannot drift from the public API.
/// </summary>
[ZemaxToolType]
public sealed class ToolCatalogTool
{
    public sealed record ToolGroup(string Id, string Title, string Purpose, int ToolCount);
    public sealed record ToolEntry(string Name, string Group, string Risk, string Description, string SafetyGuidance);
    public sealed record TaskPlaybook(
        string Id, string Goal, string Guidance,
        IReadOnlyList<string> AvailableSteps, IReadOnlyList<string> UnavailableSteps);
    public sealed record CatalogResult(
        string RecommendedWorkflow,
        int TotalTools,
        int HighImpactTools,
        IReadOnlyList<ToolGroup> Groups,
        IReadOnlyList<ToolEntry> Tools,
        IReadOnlyList<TaskPlaybook> Playbooks);

    [ZemaxTool(Name = "zemax_tool_catalog")]
    [Description("Find a small, ordered tool playbook for common optical jobs. Set task to clipping, imaging, straylight, energy, optimize or tolerance; omit it for the full catalog. Playbooks are planning hints, not automatic execution or safety authorization.")]
    public CatalogResult Execute(
        [Description("When true, return only high-impact operations that deserve an explicit confirmation.")] bool highImpactOnly = false,
        [Description("Optional task playbook: clipping, imaging, straylight, energy, optimize or tolerance. Empty returns the full catalog.")] string? task = null)
    {
        var profile = ToolsetCatalog.NormalizeProfile(Environment.GetEnvironmentVariable("ZEMAX_MCP_TOOLSET") ?? ToolsetCatalog.FullExpert);
        var readOnly = string.Equals(Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY"), "1", StringComparison.Ordinal);
        var playbooks = ToolCatalog.GetPlaybooks(task, name => StaticToolManifest.IsAllowed(profile, name, readOnly));
        var selectedNames = string.IsNullOrWhiteSpace(task) ? null
            : new HashSet<string>(playbooks.SelectMany(item => item.AvailableSteps), StringComparer.Ordinal);
        var entries = ToolCatalog.Build(highImpactOnly)
            .Where(entry => StaticToolManifest.IsAllowed(profile, entry.Name, readOnly) &&
                (selectedNames == null || selectedNames.Contains(entry.Name)))
            .ToArray();
        var groups = ToolCatalog.Groups
            .Select(group => new ToolGroup(group.Id, group.Title, group.Purpose, entries.Count(entry => entry.Group == group.Title)))
            .Where(group => group.ToolCount > 0)
            .ToArray();
        var highImpact = entries.Count(entry => entry.Risk == ToolCatalog.HighImpactRisk);

        return new CatalogResult(
            "Inspect the current system first, edit only the required data, run an analysis to verify the change, then save or export deliberately. For high-impact tools, confirm the target system and intended change before running.",
            entries.Length,
            highImpact,
            groups,
            entries,
            playbooks);
    }
}

internal static class ToolCatalog
{
    private sealed record PlaybookDefinition(string Id, string Goal, string Guidance, string[] Steps);

    private static readonly PlaybookDefinition[] PlaybookDefinitions =
    {
        new("clipping", "Locate blocked rays and pupil losses",
            "Read the lens first. Compare vignetting codes, aperture clear fraction and field/wavelength; do not interpret failed rays as blocked rays.",
            new[] { "zemax_get_system", "zemax_get_surface_aperture", "zemax_ray_trace_diagnostics", "zemax_aperture_throughput" }),
        new("imaging", "Diagnose image formation",
            "Select a field and wavelength, then compare RMS spot, PSF and MTF before changing a lens.",
            new[] { "zemax_get_system", "zemax_get_field_settings", "zemax_rms_spot", "zemax_fft_psf", "zemax_fft_mtf" }),
        new("straylight", "Inspect NSC geometry and trace",
            "Check source, detector and object IDs before ray tracing. Detector totals do not by themselves prove source-to-detector efficiency.",
            new[] { "zemax_get_nonsequential_system_settings", "zemax_nsc_scene_summary", "zemax_get_nsc_objects", "zemax_run_nsc_ray_trace", "zemax_get_nsc_detector" }),
        new("energy", "Build a traceable optical energy budget",
            "Use aperture fraction for sequential geometric clipping. For NSC, read detector flux and provide a measured/defined launched-flux denominator; detect overlapping detectors and wavelength-mixing.",
            new[] { "zemax_aperture_throughput", "zemax_nsc_scene_summary", "zemax_get_nsc_detector", "zemax_nsc_energy_budget" }),
        new("optimize", "Safely improve performance",
            "Record the starting merit and system snapshot, inspect variables, optimize, and independently compare results before saving.",
            new[] { "zemax_get_system", "zemax_get_merit_function", "zemax_get_variables", "zemax_snapshot_list", "zemax_optimize" }),
        new("tolerance", "Assess manufacturing sensitivity",
            "Inspect TDE operands and criterion, run bounded Monte Carlo, and report sample count plus unverified manufacturing assumptions.",
            new[] { "zemax_tolerance_summary", "zemax_get_tolerances", "zemax_run_tolerancing" })
    };

    internal static IReadOnlyList<ToolCatalogTool.TaskPlaybook> GetPlaybooks(string? task, Func<string, bool> permitted)
    {
        var target = task?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(target) && !PlaybookDefinitions.Any(item => item.Id == target))
            throw new ArgumentException("Unknown task. Choose clipping, imaging, straylight, energy, optimize or tolerance.", nameof(task));
        return PlaybookDefinitions.Where(item => string.IsNullOrEmpty(target) || item.Id == target)
            .Select(item => new ToolCatalogTool.TaskPlaybook(
                item.Id, item.Goal, item.Guidance,
                item.Steps.Where(permitted).ToArray(),
                item.Steps.Where(name => !permitted(name)).ToArray()))
            .ToArray();
    }

    internal const string HighImpactRisk = "High impact";
    private const string CautionRisk = "Caution";
    private const string ReadOnlyRisk = "Read-only";

    internal sealed record GroupDefinition(string Id, string Title, string Purpose);

    internal static readonly IReadOnlyList<GroupDefinition> Groups = ToolsetCatalog.Domains
        .Select(domain => new GroupDefinition(domain.Id, domain.Title, domain.Purpose))
        .ToArray();

    internal static IReadOnlyList<ToolCatalogTool.ToolEntry> Build(bool highImpactOnly)
    {
        return StaticToolManifest.All
            .Select(CreateEntry)
            .Where(entry => !highImpactOnly || entry.Risk == HighImpactRisk)
            .OrderBy(entry => GroupOrder(entry.Group))
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static ToolCatalogTool.ToolEntry CreateEntry(ToolManifestEntry tool)
    {
        var risk = tool.Impact switch
        {
            "HighImpact" => HighImpactRisk,
            "Caution" => CautionRisk,
            _ => ReadOnlyRisk
        };
        var group = ToolsetCatalog.GetDomain(tool.Name).Title;
        return new ToolCatalogTool.ToolEntry(tool.Name, group, risk, tool.Description, GetSafetyGuidance(risk, tool.Name));
    }

    private static string GetSafetyGuidance(string risk, string name)
    {
        if (risk == HighImpactRisk)
            return "Confirm the target system and intended change. Read-only mode blocks recognized lens changes; recognized ZOS-API mutations create a pre-change snapshot.";
        if (risk == CautionRisk)
            return name == "zemax_open_file"
                ? "Changes the active OpticStudio system. Confirm unsaved work has been handled first."
                : "May change connection, session, or background-job state; confirm it is safe to interrupt the current workflow.";
        return "Designed to inspect state or calculate results without intentionally editing lens data.";
    }

    private static int GroupOrder(string group) => ToolsetCatalog.GetDomainOrder(group);
}
