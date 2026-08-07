using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.PrivateRpcTests;

internal static class WorkerToolRegistrySchemaAssertions
{
    [ModuleInitializer]
    internal static void VerifyWorkerToolSchemaContract()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var registry = new WorkerToolRegistry(services);
        if (!registry.Tools.TryGetValue("zemax_test_schema_contract", out var tool))
            throw new InvalidOperationException("Worker schema regression tool was not discovered.");

        var schema = tool.InputSchema;
        var properties = schema.GetProperty("properties");
        var required = schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);

        if (!required.SetEquals(new[] { "filePath", "fields" }))
            throw new InvalidOperationException("Worker schema required parameters no longer match runtime binding semantics.");
        if (properties.TryGetProperty("cancellationToken", out _))
            throw new InvalidOperationException("CancellationToken must not be exposed in Worker tool schemas.");

        var mode = properties.GetProperty("mode");
        if (mode.GetProperty("type").GetString() != "string" || mode.GetProperty("default").GetString() != "Fast" ||
            !mode.GetProperty("enum").EnumerateArray().Select(value => value.GetString()).SequenceEqual(new[] { "Fast", "Accurate" }))
            throw new InvalidOperationException("Enum Worker schema metadata is invalid.");

        var fields = properties.GetProperty("fields");
        if (fields.GetProperty("type").GetString() != "array")
            throw new InvalidOperationException("Collection Worker parameters must publish array schemas.");
        var item = fields.GetProperty("items");
        var itemProperties = item.GetProperty("properties");
        var itemRequired = item.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
        if (!itemRequired.SetEquals(new[] { "x", "y" }) || !itemProperties.TryGetProperty("weight", out var weight) ||
            Math.Abs(weight.GetProperty("default").GetDouble() - 1.0) > double.Epsilon)
            throw new InvalidOperationException("Nested record Worker schemas must preserve camelCase names, required fields, and defaults.");

        var coefficients = properties.GetProperty("coefficients");
        if (coefficients.GetProperty("type").GetString() != "object" ||
            coefficients.GetProperty("additionalProperties").GetProperty("type").GetString() != "number")
            throw new InvalidOperationException("String-keyed dictionaries must publish object schemas with typed additional properties.");
    }
}

[ZemaxToolType]
public sealed class WorkerSchemaProbeTool
{
    public enum ProbeMode
    {
        Fast,
        Accurate
    }

    public sealed record FieldDefinition(double X, double Y, double Weight = 1.0);

    [ZemaxTool(Name = "zemax_test_schema_contract")]
    public Task<object> ExecuteAsync(
        string filePath,
        List<FieldDefinition> fields,
        ProbeMode mode = ProbeMode.Fast,
        Dictionary<string, double>? coefficients = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<object>(new { filePath, fields, mode, coefficients });
}
