using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>
/// Gives MCP clients a stable, task-oriented map of the tools compiled into this server.
/// Tool names remain unchanged; the map is deliberately derived from attributes so it cannot
/// silently drift when a new tool is registered.
/// </summary>
[McpServerToolType]
public sealed class ToolCatalogTool
{
    public sealed record ToolGroup(string Id, string Title, string Purpose, int ToolCount);
    public sealed record ToolEntry(string Name, string Group, string Risk, string Description, string SafetyGuidance);
    public sealed record CatalogResult(
        string RecommendedWorkflow,
        int TotalTools,
        int HighImpactTools,
        IReadOnlyList<ToolGroup> Groups,
        IReadOnlyList<ToolEntry> Tools);

    [McpServerTool(Name = "zemax_tool_catalog")]
    [Description("List installed Zemax MCP tools by task group and safety level. Call this before a broad task to select the smallest suitable tool; set highImpactOnly to true to review operations that can modify lens data, files, or optimization state.")]
    public CatalogResult Execute(
        [Description("When true, return only high-impact operations that deserve an explicit confirmation.")] bool highImpactOnly = false)
    {
        var entries = ToolCatalog.Build(highImpactOnly);
        var groups = ToolCatalog.Groups
            .Select(group => new ToolGroup(group.Id, group.Title, group.Purpose, entries.Count(entry => entry.Group == group.Title)))
            .Where(group => group.ToolCount > 0)
            .ToArray();
        var highImpact = entries.Count(entry => entry.Risk == ToolCatalog.HighImpactRisk);

        return new CatalogResult(
            "Inspect the current system first, edit only the required data, run an analysis to verify the change, then save or export deliberately. For high-impact tools, confirm the target system and intended change before running.",
            entries.Count,
            highImpact,
            groups,
            entries);
    }
}

internal static class ToolCatalog
{
    internal const string HighImpactRisk = "High impact";
    private const string CautionRisk = "Caution";
    private const string ReadOnlyRisk = "Read-only";

    internal sealed record GroupDefinition(string Id, string Title, string Purpose);

    internal static readonly IReadOnlyList<GroupDefinition> Groups = new[]
    {
        new GroupDefinition("system-information", "System information", "Connection, settings, catalog, tolerance, and job inspection."),
        new GroupDefinition("lens-editing", "Lens editing", "Sequential, non-sequential, field, wavelength, and configuration work."),
        new GroupDefinition("analysis", "Analysis", "Optical performance calculations and result export."),
        new GroupDefinition("optimization", "Optimization", "Merit-function work, optimization, and background-job control."),
        new GroupDefinition("files", "Files", "Opening, saving, importing, and exporting project artifacts.")
    };

    internal static IReadOnlyList<ToolCatalogTool.ToolEntry> Build(bool highImpactOnly)
    {
        var entries = typeof(ToolCatalog).Assembly
            .GetTypes()
            .Where(type => type.Namespace != null && type.Namespace.StartsWith("ZemaxMCP.Server.Tools.", StringComparison.Ordinal))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(method => new { Type = type, Method = method, Attribute = method.GetCustomAttribute<McpServerToolAttribute>() }))
            .Where(item => item.Attribute != null && !string.IsNullOrWhiteSpace(item.Attribute.Name))
            .Select(item => CreateEntry(item.Type, item.Method, item.Attribute!.Name!))
            .Where(entry => !highImpactOnly || entry.Risk == HighImpactRisk)
            .OrderBy(entry => GroupOrder(entry.Group))
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        return entries;
    }

    private static ToolCatalogTool.ToolEntry CreateEntry(Type type, MethodInfo method, string name)
    {
        var risk = GetRisk(name);
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "No additional description is available.";
        return new ToolCatalogTool.ToolEntry(name, GetGroup(type, name), risk, description, GetSafetyGuidance(risk, name));
    }

    private static string GetGroup(Type type, string name)
    {
        if (name is "zemax_open_file" or "zemax_save_file" or "zemax_new_system" or "zemax_export_analysis" or "zemax_export_glass_catalog" or
            "zemax_load_merit_function_file" or "zemax_save_merit_function_file") return "Files";

        var toolNamespace = type.Namespace ?? string.Empty;
        if (toolNamespace.Contains(".Analysis", StringComparison.Ordinal)) return "Analysis";
        if (toolNamespace.Contains(".Optimization", StringComparison.Ordinal) || toolNamespace.Contains(".Jobs", StringComparison.Ordinal)) return "Optimization";
        if (toolNamespace.Contains(".LensData", StringComparison.Ordinal) || toolNamespace.Contains(".NonSequential", StringComparison.Ordinal) ||
            toolNamespace.Contains(".Configuration", StringComparison.Ordinal) || toolNamespace.Contains(".Tolerancing", StringComparison.Ordinal)) return "Lens editing";
        return "System information";
    }

    private static string GetRisk(string name)
    {
        if (name.StartsWith("zemax_set_", StringComparison.Ordinal) ||
            name.StartsWith("zemax_add_", StringComparison.Ordinal) ||
            name.StartsWith("zemax_delete_", StringComparison.Ordinal) ||
            name.StartsWith("zemax_remove_", StringComparison.Ordinal) ||
            name.StartsWith("zemax_clear_", StringComparison.Ordinal) ||
            name.StartsWith("zemax_calculate_", StringComparison.Ordinal) ||
            name is "zemax_new_system" or "zemax_save_file" or "zemax_load_merit_function_file" or "zemax_save_merit_function_file" or
                "zemax_optimize" or "zemax_constrained_optimize" or "zemax_global_search" or "zemax_hammer_optimization" or
                "zemax_multistart_optimize" or "zemax_quick_focus" or "zemax_scale_lens" or "zemax_optimization_wizard" or
                "zemax_forbes_merit_function") return HighImpactRisk;

        if (name is "zemax_open_file" or "zemax_connect" or "zemax_disconnect" or "zemax_restart" or "zemax_job_cancel" or "zemax_multistart_stop")
            return CautionRisk;
        return ReadOnlyRisk;
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

    private static int GroupOrder(string group) => group switch
    {
        "System information" => 0,
        "Lens editing" => 1,
        "Analysis" => 2,
        "Optimization" => 3,
        "Files" => 4,
        _ => 99
    };
}
