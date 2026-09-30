using System.Runtime.CompilerServices;
using System.Text.Json;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.PrivateRpcTests;

internal static class StaticToolManifestAssertions
{
    [ModuleInitializer]
    internal static void VerifyStaticToolManifestContract()
    {
        if (StaticToolManifest.All.Count != 132)
            throw new InvalidOperationException("Static Host tool manifest must contain all 132 Worker commands.");
        if (StaticToolManifest.ContractFingerprint.Length != 64 ||
            StaticToolManifest.ContractFingerprint.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("Static tool contract fingerprint must be a SHA-256 hex digest.");

        var openFileEntry = StaticToolManifest.GetRequired("zemax_open_file");
        if (openFileEntry.DomainId != "files" || openFileEntry.Impact != "Caution")
            throw new InvalidOperationException("Static manifest must carry explicit domain and impact metadata.");
        var openFile = openFileEntry.InputSchema;
        var openFileRequired = openFile.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!openFileRequired.SetEquals(new[] { "filePath" }) ||
            openFile.GetProperty("properties").GetProperty("filePath").GetProperty("type").GetString() != "string")
            throw new InvalidOperationException("zemax_open_file must advertise filePath as a required string.");

        var statusEntry = StaticToolManifest.GetRequired("zemax_status");
        if (statusEntry.DomainId != "administration" || statusEntry.Impact != "ReadOnly" ||
            !StaticToolManifest.IsAllowed("full-expert", statusEntry.Name, readOnly: true))
            throw new InvalidOperationException("Read-only policy metadata must be available directly from the static manifest.");
        if (!StaticToolManifest.IsAllowed("full-expert", openFileEntry.Name, readOnly: true))
            throw new InvalidOperationException("Global read-only mode must preserve Caution session operations.");
        if (StaticToolManifest.IsAllowed("full-expert", "zemax_set_surface", readOnly: true))
            throw new InvalidOperationException("Global read-only mode must reject HighImpact tools.");
        if (StaticToolManifest.IsAllowed("basic-viewing", openFileEntry.Name, readOnly: false))
            throw new InvalidOperationException("The basic-viewing profile must remain stricter than the global read-only switch.");

        var expectedProfileCounts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["basic-viewing"] = 34,
            ["sequential-design"] = 76,
            ["nonsequential-stray-light"] = 19,
            ["optimization-tolerance"] = 63,
            ["full-expert"] = 132
        };
        foreach (var pair in expectedProfileCounts)
        {
            var actual = StaticToolManifest.All.Count(tool => StaticToolManifest.IsAllowed(pair.Key, tool.Name, readOnly: false));
            if (actual != pair.Value)
                throw new InvalidOperationException($"Tool profile {pair.Key} exposes {actual} tools; expected {pair.Value}. Profiles must remain task-sized and explicitly reviewed.");
        }
        if (StaticToolManifest.IsAllowed("nonsequential-stray-light", "zemax_set_surface", readOnly: false) ||
            StaticToolManifest.IsAllowed("optimization-tolerance", "zemax_get_nsc_objects", readOnly: false))
            throw new InvalidOperationException("Focused profiles leaked unrelated editor domains back into tools/list.");

        var nscTrace = StaticToolManifest.GetRequired("zemax_run_nsc_ray_trace").InputSchema.GetProperty("properties");
        if (nscTrace.GetProperty("clearDetectors").GetProperty("default").GetBoolean() != true ||
            nscTrace.GetProperty("timeoutSeconds").GetProperty("default").GetDouble() != 60 ||
            nscTrace.GetProperty("runInBackground").GetProperty("default").GetBoolean() != true)
            throw new InvalidOperationException("Managed NSC ray trace must preserve safe detector clearing, timeout, and background defaults.");

        var batchSurfaces = StaticToolManifest.GetRequired("zemax_batch_set_surfaces").InputSchema;
        var batchRequired = batchSurfaces.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!batchRequired.SetEquals(new[] { "edits" }))
            throw new InvalidOperationException("zemax_batch_set_surfaces must require only the edits array.");
        var batchItems = batchSurfaces.GetProperty("properties").GetProperty("edits").GetProperty("items");
        var batchItemRequired = batchItems.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!batchItemRequired.SetEquals(new[] { "surfaceNumber" }) ||
            !batchItems.GetProperty("properties").TryGetProperty("thickness", out _) ||
            !batchItems.GetProperty("properties").TryGetProperty("isStop", out _))
            throw new InvalidOperationException("Batch surface-edit schema must preserve required surfaceNumber and nullable edit fields.");

        var rayDiagnostics = StaticToolManifest.GetRequired("zemax_ray_trace_diagnostics").InputSchema.GetProperty("properties");
        if (rayDiagnostics.GetProperty("fieldSampling").GetProperty("default").GetInt32() != 5 ||
            rayDiagnostics.GetProperty("pupilSampling").GetProperty("default").GetInt32() != 5 ||
            rayDiagnostics.GetProperty("maxFailures").GetProperty("default").GetInt32() != 50)
            throw new InvalidOperationException("Ray-trace diagnostics must preserve bounded sampling defaults.");

        var setFields = StaticToolManifest.GetRequired("zemax_set_fields").InputSchema;
        var setFieldsRequired = setFields.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!setFieldsRequired.SetEquals(new[] { "fields" }))
            throw new InvalidOperationException("zemax_set_fields must advertise fields as required and fieldType as optional.");
        var fields = setFields.GetProperty("properties").GetProperty("fields");
        if (fields.GetProperty("type").GetString() != "array")
            throw new InvalidOperationException("zemax_set_fields.fields must be an array.");
        var item = fields.GetProperty("items");
        var itemRequired = item.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        var itemProperties = item.GetProperty("properties");
        if (!itemRequired.SetEquals(new[] { "x", "y" }) ||
            !itemProperties.TryGetProperty("weight", out var weight) ||
            Math.Abs(weight.GetProperty("default").GetDouble() - 1.0) > double.Epsilon)
            throw new InvalidOperationException("Nested field schemas must preserve camelCase names, required values, and defaults.");

        var optimize = StaticToolManifest.GetRequired("zemax_optimize").InputSchema.GetProperty("properties");
        if (optimize.GetProperty("algorithm").GetProperty("default").GetString() != "DLS" ||
            optimize.GetProperty("cycles").GetProperty("default").GetInt32() != 0)
            throw new InvalidOperationException("Static manifest must preserve tool parameter defaults.");

        if (StaticToolManifest.All.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != StaticToolManifest.All.Count)
            throw new InvalidOperationException("Static Host tool manifest contains duplicate tool names.");

        var opaque = new List<string>();
        foreach (var tool in StaticToolManifest.All)
            FindOpaqueObjects(tool.InputSchema, tool.Name + ".input", opaque);
        if (opaque.Count > 0)
            throw new InvalidOperationException("Generated tool schemas contain unresolved opaque object contracts: " + string.Join(", ", opaque.Take(20)));
    }

    private static void FindOpaqueObjects(JsonElement schema, string path, List<string> opaque)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var type) && type.GetString() == "object")
        {
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            var hasAdditional = schema.TryGetProperty("additionalProperties", out _);
            if (!hasProperties && !hasAdditional) opaque.Add(path);
            if (hasProperties)
                foreach (var property in properties.EnumerateObject())
                    FindOpaqueObjects(property.Value, path + "." + property.Name, opaque);
            if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object)
                FindOpaqueObjects(additional, path + ".additionalProperties", opaque);
        }
        if (schema.TryGetProperty("items", out var items)) FindOpaqueObjects(items, path + "[]", opaque);
    }
}
