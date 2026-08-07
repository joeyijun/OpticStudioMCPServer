using System.Runtime.CompilerServices;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.PrivateRpcTests;

internal static class StaticToolManifestAssertions
{
    [ModuleInitializer]
    internal static void VerifyStaticToolManifestContract()
    {
        if (StaticToolManifest.All.Count != 126)
            throw new InvalidOperationException("Static Host tool manifest must contain all 126 Worker commands.");

        var openFileEntry = StaticToolManifest.GetRequired("zemax_open_file");
        if (openFileEntry.DomainId != "files" || openFileEntry.Impact != "HighImpact")
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
        if (StaticToolManifest.IsAllowed("full-expert", openFileEntry.Name, readOnly: true))
            throw new InvalidOperationException("Static manifest read-only admission must reject high-impact tools.");

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
    }
}
