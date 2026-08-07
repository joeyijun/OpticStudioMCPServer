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
    public sealed record CatalogResult(
        string RecommendedWorkflow,
        int TotalTools,
        int HighImpactTools,
        IReadOnlyList<ToolGroup> Groups,
        IReadOnlyList<ToolEntry> Tools);

    [ZemaxTool(Name = "zemax_tool_catalog")]
    [Description("List installed Zemax MCP tools by task group and safety level. Call this before a broad task to select the smallest suitable tool; set highImpactOnly to true to review operations that can modify lens data, files, or optimization state.")]
    public CatalogResult Execute(
        [Description("When true, return only high-impact operations that deserve an explicit confirmation.")] bool highImpactOnly = false)
    {
        var profile = ToolsetCatalog.NormalizeProfile(Environment.GetEnvironmentVariable("ZEMAX_MCP_TOOLSET") ?? ToolsetCatalog.FullExpert);
        var readOnly = string.Equals(Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY"), "1", StringComparison.Ordinal);
        var entries = ToolCatalog.Build(highImpactOnly)
            .Where(entry => StaticToolManifest.IsAllowed(profile, entry.Name, readOnly))
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
            entries);
    }
}

internal static class ToolCatalog
{
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
