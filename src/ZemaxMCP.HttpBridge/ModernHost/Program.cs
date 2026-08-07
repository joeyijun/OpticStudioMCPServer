using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Serilog;
using ZemaxMCP.Rpc;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Public product boundary. ModelContextProtocol.AspNetCore owns HTTP,
/// Streamable HTTP, negotiation, SSE and protocol compatibility; this project
/// contains no hand-written MCP JSON-RPC dispatcher.
/// </summary>
internal static class Program
{
    private const string ClientInstanceMetaKey = "io.zemaxmcp/clientInstanceId";
    private const string ClientInstanceHeader = "X-Zemax-MCP-Client-Instance";

    public static async Task<int> Main(string[] args)
    {
        HostOptions options;
        try { options = HostOptions.Parse(args); }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        Directory.CreateDirectory(options.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(options.LogDirectory, "http-host-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
            builder.WebHost.UseUrls("http://" + options.Host + ":" + options.Port);
            builder.WebHost.UseSetting("AllowedHosts", string.Join(";", options.AllowedHosts));
            builder.Host.UseSerilog();
            builder.Services.AddSingleton(options);
            var workerClient = new WorkerRpcClient(options);
            builder.Services.AddSingleton(workerClient);
            var controlLease = new OpticStudioControlLease();
            builder.Services.AddSingleton(controlLease);
            var activity = new McpActivityMonitor();
            builder.Services.AddSingleton(activity);
            builder.Services
                .AddMcpServer(server => server.ServerInfo = new()
                {
                    Name = "zemax-mcp",
                    Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown"
                })
                .WithHttpTransport(transport => transport.Stateless = true)
                .WithListToolsHandler(async (_, _) =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return new ListToolsResult
                    {
                        Tools = StaticToolManifest.All
                            .Where(entry => StaticToolManifest.IsAllowed(options.Toolset, entry.Name, options.ReadOnly))
                            .Select(entry => new Tool
                            {
                                Name = entry.Name,
                                Description = entry.Description,
                                InputSchema = entry.InputSchema
                            })
                            .ToList()
                    };
                })
                .WithCallToolHandler(async (request, cancellationToken) =>
                {
                    if (!StaticToolManifest.IsAllowed(options.Toolset, request.Params.Name, options.ReadOnly))
                    {
                        return new CallToolResult
                        {
                            Content = new List<ContentBlock>
                            {
                                new TextContentBlock
                                {
                                    Text = "The selected toolset/read-only policy does not permit " + request.Params.Name + "."
                                }
                            },
                            IsError = true
                        };
                    }

                    var clientId = ResolveControlIdentity(request);
                    using var call = activity.Begin(clientId, request.Params.Name);
                    using var lease = await controlLease.AcquireAsync(clientId, request.Params.Name, cancellationToken).ConfigureAwait(false);

                    Func<OperationProgress, CancellationToken, Task>? progressHandler = null;
                    if (request.Params.ProgressToken is { } progressToken)
                    {
                        progressHandler = async (progress, progressCancellation) =>
                        {
                            // Only publish fraction-based updates as MCP progress;
                            // queue-position/job lifecycle events remain available
                            // through structured Worker event state and health.
                            if (!progress.Fraction.HasValue) return;
                            var percent = Math.Clamp((float)(progress.Fraction.Value * 100.0), 0f, 100f);
                            await request.Server.NotifyProgressAsync(progressToken, new ProgressNotificationValue
                            {
                                Progress = percent,
                                Total = 100f,
                                Message = progress.Message ?? progress.State
                            }, cancellationToken: progressCancellation).ConfigureAwait(false);
                        };
                    }

                    return await workerClient.CallToolAsync(request.Params, cancellationToken, progressHandler).ConfigureAwait(false);
                });

            var app = builder.Build();
            var worker = app.Services.GetRequiredService<WorkerRpcClient>();

            app.Use(async (context, next) =>
            {
                if (!OriginPolicy.TryApply(context, options.AllowedOrigins)) return;
                if (HttpMethods.IsOptions(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;
                }
                if (context.Request.Path.StartsWithSegments(options.McpPath) && !HasValidToken(context, options.AccessToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }

                var instanceHeader = context.Request.Headers[ClientInstanceHeader].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(instanceHeader) && !IsSafeClientInstanceId(instanceHeader))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsync("Invalid X-Zemax-MCP-Client-Instance header.").ConfigureAwait(false);
                    return;
                }

                var claims = new List<Claim>
                {
                    new("zemax-mcp-auth-profile", string.IsNullOrWhiteSpace(options.AccessToken) ? "local" : "shared-token"),
                    new("zemax-mcp-remote-endpoint", context.Connection.RemoteIpAddress?.ToString() ?? "local")
                };
                if (!string.IsNullOrWhiteSpace(instanceHeader)) claims.Add(new Claim("zemax-mcp-client-instance", instanceHeader));
                var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sessionId)) claims.Add(new Claim("zemax-mcp-session-id", HashIdentityComponent(sessionId)));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "zemax-mcp-token"));
                await next().ConfigureAwait(false);
            });

            app.MapGet(options.McpPath + "/health", async (CancellationToken cancellationToken) =>
            {
                WorkerStatus? status = null;
                try { status = await worker.GetStatusAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warning(ex, "Worker health RPC failed"); }
                var activityHealth = activity.GetHealth();
                return Results.Json(new
                {
                    bridgeRunning = true,
                    mcpServerRunning = status != null,
                    rpcVersion = ZemaxRpcProtocol.Version,
                    manifestFingerprint = StaticToolManifest.ContractFingerprint,
                    workerRpcVersion = status?.RpcVersion,
                    workerManifestFingerprint = status?.ManifestFingerprint,
                    toolset = options.Toolset,
                    zosApiLoaded = status?.ZosApiLoaded ?? false,
                    zosApiConnected = status?.Connected ?? false,
                    licenseStatus = status?.CurrentLicenseStatus ?? status?.LastLicenseStatus ?? "Not validated",
                    licenseValidForApi = status?.LicenseValidForApi,
                    lastConnectionError = status?.LastConnectionError,
                    zemaxDataDirectory = status?.OpticStudioDataDirectory ?? "Not reported",
                    loadedZosApiFiles = new { zosApi = status?.ZosApiAssembly },
                    authenticationRequired = !string.IsNullOrWhiteSpace(options.AccessToken),
                    originValidationEnabled = true,
                    readOnly = options.ReadOnly,
                    snapshotDirectory = status?.SnapshotDirectory ?? options.SnapshotDirectory,
                    lastSnapshotPath = status?.LastSnapshotPath,
                    jobs = status?.Jobs ?? Array.Empty<WorkerJobStatus>(),
                    requestTimeoutSeconds = options.RequestTimeoutSeconds,
                    requestWriteTimeoutSeconds = options.RequestWriteTimeoutSeconds,
                    hardRecoveryTimeoutSeconds = options.HardRecoveryTimeoutSeconds,
                    cancellationWriteTimeoutSeconds = options.CancellationWriteTimeoutSeconds,
                    lastClient = activityHealth.LastClient,
                    lastRequestAt = activityHealth.LastRequestAt,
                    activeRequests = activityHealth.ActiveRequests,
                    activeOperations = activityHealth.ActiveRequests == 0 ? Array.Empty<object>() : new[] { new { tool = activityHealth.LastTool, startedAt = activityHealth.ActiveSince } },
                    clients = activityHealth.LastRequestAt == null ? Array.Empty<object>() : new[] { new { name = activityHealth.LastClient, lastRequestAt = activityHealth.LastRequestAt } },
                    worker = worker.GetHealth(),
                    controlLease = controlLease.GetHealth(),
                    activity = activityHealth
                });
            });
            app.MapMcp(options.McpPath);

            Log.Information("Official MCP ASP.NET Core Host listening at {Endpoint}; private RPC v{RpcVersion}, manifest {ManifestFingerprint}",
                "http://" + options.Host + ":" + options.Port + options.McpPath,
                ZemaxRpcProtocol.Version,
                StaticToolManifest.ContractFingerprint);
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "MCP Host terminated unexpectedly");
            return 1;
        }
        finally { await Log.CloseAndFlushAsync().ConfigureAwait(false); }
    }

    private static bool HasValidToken(HttpContext context, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var presented = Encoding.UTF8.GetBytes(header.Substring("Bearer ".Length));
        var expected = Encoding.UTF8.GetBytes(token);
        return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static string ResolveControlIdentity(ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request)
    {
        var profile = request.User?.FindFirst("zemax-mcp-auth-profile")?.Value;
        if (!string.IsNullOrWhiteSpace(profile) &&
            !string.Equals(profile, "shared-token", StringComparison.Ordinal) &&
            !string.Equals(profile, "local", StringComparison.Ordinal))
            return "token:" + profile;

        var clientInfo = request.Server?.ClientInfo;
        var name = string.IsNullOrWhiteSpace(clientInfo?.Name) ? "unknown" : clientInfo!.Name.Trim();
        var version = string.IsNullOrWhiteSpace(clientInfo?.Version) ? "unknown" : clientInfo!.Version.Trim();
        var endpoint = request.User?.FindFirst("zemax-mcp-remote-endpoint")?.Value ?? "unknown";
        var instanceId = GetRequestClientInstanceId(request.Params.Meta)
            ?? request.User?.FindFirst("zemax-mcp-client-instance")?.Value;
        if (!string.IsNullOrWhiteSpace(instanceId))
            return $"client:{name}@{version}|instance:{instanceId}|remote:{endpoint}";

        var sessionId = request.User?.FindFirst("zemax-mcp-session-id")?.Value;
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"client:{name}@{version}|session:{sessionId}|remote:{endpoint}";

        return $"client:{name}@{version}|remote:{endpoint}";
    }

    private static string? GetRequestClientInstanceId(JsonObject? meta)
    {
        if (meta == null || !meta.TryGetPropertyValue(ClientInstanceMetaKey, out var node) || node is not JsonValue value ||
            !value.TryGetValue<string>(out var instanceId) || !IsSafeClientInstanceId(instanceId)) return null;
        return instanceId;
    }

    private static bool IsSafeClientInstanceId(string value) =>
        value.Length is >= 1 and <= 128 && value.All(character =>
            character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private static string HashIdentityComponent(string value)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
