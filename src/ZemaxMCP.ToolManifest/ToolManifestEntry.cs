using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Stable SHA-256 fingerprint of the complete public tool contract. Host
    /// and Worker compare this during their private-pipe handshake so mixed
    /// binaries cannot silently execute against different schemas/policies.
    /// </summary>
    public static string ContractFingerprint { get; } = ComputeContractFingerprint();

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
        // Preserve the established global read-only semantics: HighImpact
        // operations are blocked, while Caution session/connection operations
        // remain available. Profiles such as basic-viewing can independently
        // restrict the surface to ReadOnly impact only.
        return !readOnly || !string.Equals(entry.Impact, ToolsetCatalog.ToolImpact.HighImpact.ToString(), StringComparison.Ordinal);
    }

    private static string ComputeContractFingerprint()
    {
        var canonical = new StringBuilder();
        foreach (var entry in GeneratedToolManifestData.Entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            AppendField(canonical, entry.Name);
            AppendField(canonical, entry.Description);
            AppendField(canonical, entry.DomainId);
            AppendField(canonical, entry.Impact);
            AppendField(canonical, entry.InputSchema.GetRawText());
            canonical.Append('\n');
        }

        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
        return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void AppendField(StringBuilder target, string value)
    {
        // Length-prefixing prevents ambiguous concatenation while keeping the
        // fingerprint independent of JSON serialization options elsewhere.
        target.Append(value.Length).Append(':').Append(value).Append('|');
    }
}
