using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
            builder.Host.UseSerilog();
            builder.Services.AddSingleton(options);
            var workerClient = new WorkerRpcClient(options);
            builder.Services.AddSingleton(workerClient);
            var controlLease = new OpticStudioControlLease();
            builder.Services.AddSingleton(controlLease);
            var activity = new McpActivityMonitor();
            builder.Services.AddSingleton(activity);
            builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
                .AllowAnyOrigin()
                .AllowAnyMethod()
                .WithHeaders("Authorization", "Content-Type", "Accept", "MCP-Protocol-Version", "Mcp-Method", "Mcp-Name", "Mcp-Session-Id")));

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
                    var clientId = request.User?.FindFirst("zemax-mcp-client")?.Value
                        ?? request.Server?.ClientInfo?.Name
                        ?? "anonymous";
                    using var call = activity.Begin(clientId, request.Params.Name);
                    using var lease = await controlLease.AcquireAsync(clientId, request.Params.Name, cancellationToken).ConfigureAwait(false);
                    return await workerClient.CallToolAsync(request.Params, cancellationToken).ConfigureAwait(false);
                });

            var app = builder.Build();
            var worker = app.Services.GetRequiredService<WorkerRpcClient>();
            await worker.StartAsync(app.Lifetime.ApplicationStopping).ConfigureAwait(false);

            app.UseCors();
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments(options.McpPath) && !HasValidToken(context, options.AccessToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    return;
                }
                var claimedClient = context.Request.Headers["Mcp-Name"].FirstOrDefault();
                if (string.IsNullOrWhiteSpace(claimedClient)) claimedClient = context.Connection.RemoteIpAddress?.ToString() ?? "local";
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("zemax-mcp-client", claimedClient) }, "zemax-mcp-token"));
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
                    licenseStatus = status?.Connected == true ? "Connected" : "Not connected",
                    zemaxDataDirectory = status?.OpticStudioDataDirectory ?? "Not reported",
                    loadedZosApiFiles = new { zosApi = status?.ZosApiAssembly },
                    authenticationRequired = !string.IsNullOrWhiteSpace(options.AccessToken),
                    originValidationEnabled = false,
                    readOnly = options.ReadOnly,
                    snapshotDirectory = options.SnapshotDirectory,
                    requestTimeoutSeconds = options.RequestTimeoutSeconds,
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
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(header.Substring("Bearer ".Length), token, StringComparison.Ordinal);
    }
}
