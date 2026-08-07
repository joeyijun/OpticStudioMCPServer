using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Serilog;
using ZemaxMCP.Rpc;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Public product boundary.  ModelContextProtocol.AspNetCore owns HTTP,
/// Streamable HTTP, negotiation, SSE and protocol compatibility; this project
/// contains no hand-written MCP JSON-RPC dispatcher.
/// </summary>
internal static class Program
{
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
            // Host filtering consumes this standard ASP.NET Core setting. It
            // is intentionally never the framework's permissive "*" default.
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
                .WithHttpTransport(transport =>
                {
                    // The SDK accepts modern stateless requests and retains
                    // legacy stateful initialization support when required.
                    transport.Stateless = true;
                })
                .WithListToolsHandler(async (_, cancellationToken) =>
                    await workerClient.ListToolsAsync(cancellationToken).ConfigureAwait(false))
                .WithCallToolHandler(async (request, cancellationToken) =>
                {
                    var clientId = ResolveControlIdentity(request);
                    using var call = activity.Begin(clientId, request.Params.Name);
                    using var lease = await controlLease.AcquireAsync(clientId, request.Params.Name, cancellationToken).ConfigureAwait(false);
                    return await workerClient.CallToolAsync(request.Params, cancellationToken).ConfigureAwait(false);
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
                // A single shared launcher token is authentication, not a
                // client identity. Keep the token profile distinct so leases
                // can fall back to client-info + remote endpoint. Per-client
                // tokens can later supply a non-shared profile here.
                var claims = new[]
                {
                    new Claim("zemax-mcp-auth-profile", string.IsNullOrWhiteSpace(options.AccessToken) ? "local" : "shared-token"),
                    new Claim("zemax-mcp-remote-endpoint", context.Connection.RemoteIpAddress?.ToString() ?? "local")
                };
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

            Log.Information("Official MCP ASP.NET Core Host listening at {Endpoint}", "http://" + options.Host + ":" + options.Port + options.McpPath);
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
        if (!string.IsNullOrWhiteSpace(profile) && !string.Equals(profile, "shared-token", StringComparison.Ordinal) && !string.Equals(profile, "local", StringComparison.Ordinal))
            return "token:" + profile;

        var clientInfo = request.Server?.ClientInfo;
        var name = clientInfo?.Name;
        var version = clientInfo?.Version;
        var endpoint = request.User?.FindFirst("zemax-mcp-remote-endpoint")?.Value ?? "unknown";
        return "client:" + (string.IsNullOrWhiteSpace(name) ? "unknown" : name.Trim()) +
            "@" + (string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim()) +
            "|remote:" + endpoint;
    }
}
