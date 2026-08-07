using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
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

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
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
            // Cancellation is supplied by the Worker runtime and is never part
            // of the public tool contract.
            if (parameter.ParameterType == typeof(CancellationToken)) continue;

            var schema = BuildTypeSchema(parameter.ParameterType, new HashSet<Type>());
            var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrWhiteSpace(description)) schema["description"] = description;
            if (parameter.HasDefaultValue) schema["default"] = NormalizeDefaultValue(parameter.ParameterType, parameter.DefaultValue);
            else required.Add(parameter.Name!);
            properties[parameter.Name!] = schema;
        }
        return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required.Count == 0 ? null : required,
            ["additionalProperties"] = false
        }.Where(pair => pair.Value != null).ToDictionary(pair => pair.Key, pair => pair.Value), JsonOptions);
    }

    private static Dictionary<string, object?> BuildTypeSchema(Type type, HashSet<Type> stack)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable != null) type = nullable;

        if (type == typeof(string) || type == typeof(char))
            return new Dictionary<string, object?> { ["type"] = "string" };
        if (type == typeof(bool))
            return new Dictionary<string, object?> { ["type"] = "boolean" };
        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
            return new Dictionary<string, object?> { ["type"] = "integer" };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new Dictionary<string, object?> { ["type"] = "number" };
        if (type.IsEnum)
            return new Dictionary<string, object?> { ["type"] = "string", ["enum"] = Enum.GetNames(type) };

        // Dictionaries also implement IEnumerable<KeyValuePair<,>>, so detect
        // them before the generic enumerable path.
        if (IsDictionary(type, out var valueType))
            return new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["additionalProperties"] = BuildTypeSchema(valueType!, stack)
            };

        var elementType = GetEnumerableElementType(type);
        if (elementType != null)
            return new Dictionary<string, object?>
            {
                ["type"] = "array",
                ["items"] = BuildTypeSchema(elementType, stack)
            };

        // Avoid infinite recursion for self-referential model types. The binder
        // can still deserialize them, while the schema keeps a safe object boundary.
        if (!stack.Add(type)) return new Dictionary<string, object?> { ["type"] = "object" };
        try
        {
            var objectProperties = new Dictionary<string, object?>();
            var required = new List<string>();
            var constructor = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .OrderByDescending(candidate => candidate.GetParameters().Length)
                .FirstOrDefault();

            if (constructor != null && constructor.GetParameters().Length > 0)
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    var rawName = parameter.Name ?? throw new InvalidOperationException("A model constructor parameter has no name.");
                    var name = JsonNamingPolicy.CamelCase.ConvertName(rawName);
                    var schema = BuildTypeSchema(parameter.ParameterType, stack);
                    var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
                    if (!string.IsNullOrWhiteSpace(description)) schema["description"] = description;
                    if (parameter.HasDefaultValue) schema["default"] = NormalizeDefaultValue(parameter.ParameterType, parameter.DefaultValue);
                    else required.Add(name);
                    objectProperties[name] = schema;
                }
            }
            else
            {
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.CanRead))
                {
                    var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                    objectProperties[name] = BuildTypeSchema(property.PropertyType, stack);
                }
            }

            var result = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = objectProperties,
                ["additionalProperties"] = false
            };
            if (required.Count > 0) result["required"] = required;
            return result;
        }
        finally { stack.Remove(type); }
    }

    private static object? NormalizeDefaultValue(Type type, object? value)
    {
        if (value == null) return null;
        var actualType = Nullable.GetUnderlyingType(type) ?? type;
        return actualType.IsEnum ? value.ToString() : value;
    }

    private static Type? GetEnumerableElementType(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        var enumerable = type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }

    private static bool IsDictionary(Type type, out Type? valueType)
    {
        var dictionary = type.GetInterfaces().Concat(new[] { type })
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) &&
                candidate.GetGenericArguments()[0] == typeof(string));
        valueType = dictionary?.GetGenericArguments()[1];
        return dictionary != null;
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
            throw new ArgumentException("Missing required argument: " + parameter.Name);
        }).ToArray();
    }
}
