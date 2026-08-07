using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.Server.Tooling;

/// <summary>Marks a class whose public attributed methods are Worker commands.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ZemaxToolTypeAttribute : Attribute { }

/// <summary>
/// Worker-owned execution metadata. Public MCP schemas are generated at build
/// time into ZemaxMCP.ToolManifest; the Worker only binds and executes commands.
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
    internal Type DeclaringType { get; set; } = null!;
    internal MethodInfo Method { get; set; } = null!;
}

/// <summary>
/// Private Worker command binder. Tool schemas come from the shared build-time
/// manifest so the public Host and runtime binder cannot drift independently.
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
        var missingRuntime = StaticToolManifest.All.Where(entry => !_tools.ContainsKey(entry.Name)).Select(entry => entry.Name).ToArray();
        if (missingRuntime.Length > 0)
            throw new InvalidOperationException("Static tool manifest contains commands without Worker implementations: " + string.Join(", ", missingRuntime));
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
            .Select(item =>
            {
                var manifest = StaticToolManifest.GetRequired(item.Attribute!.Name);
                return new WorkerToolDefinition
                {
                    Name = manifest.Name,
                    DeclaringType = item.Type,
                    Method = item.Method
                };
            });
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
