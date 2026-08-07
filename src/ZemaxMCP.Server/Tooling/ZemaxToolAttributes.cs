using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ZemaxMCP.Server.Tooling;

/// <summary>Marks a class whose public attributed methods are Worker commands.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ZemaxToolTypeAttribute : Attribute { }

/// <summary>
/// Worker-owned command metadata. This intentionally has no dependency on the
/// MCP SDK: MCP schemas and transport terminate at the .NET Host.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ZemaxToolAttribute : Attribute
{
    public ZemaxToolAttribute() { }
    public ZemaxToolAttribute(string name) => Name = name;
    public string Name { get; set; } = string.Empty;
}

public sealed class WorkerToolDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public JsonElement InputSchema { get; set; }
    internal Type DeclaringType { get; set; } = null!;
    internal MethodInfo Method { get; set; } = null!;
}

/// <summary>
/// Private Worker command catalogue and binder. It is deliberately protocol
/// neutral: JSON arguments enter from RPC, ordinary .NET results leave via RPC.
/// </summary>
public sealed class WorkerToolRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceProvider _services;
    private readonly IReadOnlyDictionary<string, WorkerToolDefinition> _tools;

    public WorkerToolRegistry(IServiceProvider services)
    {
        _services = services;
        _tools = Discover().ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, WorkerToolDefinition> Tools => _tools;

    public async Task<object?> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw new InvalidOperationException("Unknown OpticStudio tool: " + name);

        var instance = ActivatorUtilities.CreateInstance(_services, tool.DeclaringType);
        var values = BindArguments(tool.Method, arguments, cancellationToken);
        object? invocation;
        try { invocation = tool.Method.Invoke(instance, values); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }

        if (invocation is Task task)
        {
            await task.ConfigureAwait(false);
            return task.GetType().IsGenericType
                ? task.GetType().GetProperty("Result")!.GetValue(task)
                : null;
        }
        return invocation;
    }

    private static IEnumerable<WorkerToolDefinition> Discover()
    {
        return typeof(WorkerToolRegistry).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<ZemaxToolTypeAttribute>() != null)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(method => new { Type = type, Method = method, Attribute = method.GetCustomAttribute<ZemaxToolAttribute>() }))
            .Where(item => item.Attribute != null && !string.IsNullOrWhiteSpace(item.Attribute.Name))
            .Select(item => new WorkerToolDefinition
            {
                Name = item.Attribute!.Name,
                Description = item.Method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "No additional description is available.",
                InputSchema = BuildSchema(item.Method),
                DeclaringType = item.Type,
                Method = item.Method
            });
    }

    private static JsonElement BuildSchema(MethodInfo method)
    {
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();
        foreach (var parameter in method.GetParameters())
        {
            var schema = new Dictionary<string, object?>
            {
                ["type"] = JsonType(parameter.ParameterType),
                ["description"] = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description
            };
            if (parameter.HasDefaultValue) schema["default"] = parameter.DefaultValue;
            else if (!IsNullable(parameter.ParameterType)) required.Add(parameter.Name!);
            properties[parameter.Name!] = schema.Where(pair => pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required.Count == 0 ? null : required,
            ["additionalProperties"] = false
        }.Where(pair => pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value), JsonOptions);
    }

    private static object?[] BindArguments(MethodInfo method, JsonElement arguments, CancellationToken cancellationToken)
    {
        if (arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            throw new ArgumentException("Tool arguments must be a JSON object.");
        return method.GetParameters().Select(parameter =>
        {
            if (parameter.ParameterType == typeof(CancellationToken)) return (object)cancellationToken;
            if (arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(parameter.Name!, out var value))
                return JsonSerializer.Deserialize(value.GetRawText(), parameter.ParameterType, JsonOptions);
            if (parameter.HasDefaultValue) return parameter.DefaultValue;
            if (IsNullable(parameter.ParameterType)) return null;
            throw new ArgumentException("Missing required argument: " + parameter.Name);
        }).ToArray();
    }

    private static string JsonType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsEnum) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short)) return "integer";
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
        if (type.IsArray || (type.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))) return "array";
        return "object";
    }

    private static bool IsNullable(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) != null;
}
