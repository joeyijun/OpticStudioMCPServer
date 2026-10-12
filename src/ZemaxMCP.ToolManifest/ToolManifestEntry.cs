using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZemaxMCP.Toolsets;

namespace ZemaxMCP.ToolManifest;

public sealed class ToolManifestEntry
{
    public ToolManifestEntry(string name, string description, string inputSchemaJson)
    {
        Name = name;
        Description = ExecutionLocationPolicy.ToolNotice + description;
        DomainId = ToolsetCatalog.GetDomainId(name);
        Impact = ToolsetCatalog.GetImpact(name).ToString();
        BackgroundExecution = BackgroundToolPolicy.UsesSharedRunner(name) ? "shared-runner" :
            BackgroundToolPolicy.SupportsTasks(name) ? "native-job" : "none";
        var schema = JsonNode.Parse(inputSchemaJson)!;
        if (BackgroundToolPolicy.UsesSharedRunner(name))
        {
            var propertiesForJobs = schema["properties"]!.AsObject();
            if (propertiesForJobs.ContainsKey("runInBackground") || propertiesForJobs.ContainsKey("backgroundTimeoutSeconds"))
                throw new InvalidOperationException("Shared background controls collide with native parameters: " + name);
            propertiesForJobs["runInBackground"] = new JsonObject
            {
                ["type"] = "boolean", ["default"] = false,
                ["description"] = "Queue this potentially long operation as a managed Job. Ordinary clients default to synchronous compatibility; Tasks-capable clients may use background automatically when omitted. Explicit false forces a synchronous result."
            };
            propertiesForJobs["backgroundTimeoutSeconds"] = new JsonObject
            {
                ["type"] = "integer", ["default"] = 300, ["minimum"] = 1, ["maximum"] = 7200,
                ["description"] = "Background execution deadline (1-7200 seconds), followed by bounded cancellation drain/hard Worker recovery. Does not change native tool-specific timeout parameters."
            };
            Description += " Supports a managed background Job via runInBackground=true; inspect job_status or use negotiated official MCP Tasks.";
        }
        if (schema["properties"] is JsonObject properties)
            foreach (var property in properties)
                if (property.Key.EndsWith("Path", StringComparison.Ordinal) && property.Value is JsonObject parameter)
                    parameter["description"] = ExecutionLocationPolicy.PathNotice +
                        (parameter["description"]?.GetValue<string>() ?? "");
        using var document = JsonDocument.Parse(schema.ToJsonString());
        InputSchema = document.RootElement.Clone();
    }

    public string Name { get; }
    public string Description { get; }
    public string DomainId { get; }
    public string Impact { get; }
    public string BackgroundExecution { get; }
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
            AppendField(canonical, entry.BackgroundExecution);
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
