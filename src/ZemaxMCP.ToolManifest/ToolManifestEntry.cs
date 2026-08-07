using System.Text.Json;
using ZemaxMCP.Toolsets;

namespace ZemaxMCP.ToolManifest;

public sealed class ToolManifestEntry
{
    public ToolManifestEntry(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = description;
        DomainId = ToolsetCatalog.GetDomainId(name);
        Impact = ToolsetCatalog.GetImpact(name).ToString();
        using var document = JsonDocument.Parse(inputSchemaJson);
        InputSchema = document.RootElement.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public string DomainId { get; }
    public string Impact { get; }
    public JsonElement InputSchema { get; }
}

public static class StaticToolManifest
{
    private static readonly IReadOnlyDictionary<string, ToolManifestEntry> ByName =
        GeneratedToolManifestData.Entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);

    public static IReadOnlyList<ToolManifestEntry> All => GeneratedToolManifestData.Entries;

    public static bool TryGet(string name, out ToolManifestEntry entry) => ByName.TryGetValue(name, out entry!);

    public static ToolManifestEntry GetRequired(string name) =>
        ByName.TryGetValue(name, out var entry)
            ? entry
            : throw new InvalidOperationException("Tool manifest does not contain Worker command: " + name);

    public static bool IsAllowed(string profile, string toolName, bool readOnly)
    {
        if (!ByName.TryGetValue(toolName, out var entry)) return false;
        if (!ToolsetCatalog.IsToolAllowed(profile, toolName)) return false;
        return !readOnly || string.Equals(entry.Impact, ToolsetCatalog.ToolImpact.ReadOnly.ToString(), StringComparison.Ordinal);
    }
}
