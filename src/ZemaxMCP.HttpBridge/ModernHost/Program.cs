using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            var credentialStore = string.IsNullOrWhiteSpace(options.ClientCredentialsFile)
                ? null
                : new ClientCredentialStore(options.ClientCredentialsFile);
            var workerClient = new WorkerRpcClient(options);
            builder.Services.AddSingleton(workerClient);
            var controlLease = new OpticStudioControlLease();
            workerClient.JobStateChanged += controlLease.ObserveJob;
            workerClient.GenerationEnded += controlLease.ReleaseGeneration;
            var jobOwners = new JobOwnerRegistry();
            workerClient.GenerationEnded += jobOwners.ReleaseGeneration;
            builder.Services.AddSingleton(jobOwners);
            builder.Services.AddSingleton(controlLease);
            var activity = new McpActivityMonitor();
            builder.Services.AddSingleton(activity);
            var mcpBuilder = builder.Services
                .AddMcpServer(server => server.ServerInfo = new()
                {
                    Name = "zemax-mcp",
                    Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "unknown"
                })
                .WithHttpTransport(transport => transport.Stateless = true)
                .WithListToolsHandler(async (request, _) =>
                {
                    await Task.CompletedTask.ConfigureAwait(false);
                    return new ListToolsResult
                    {
                        Tools = StaticToolManifest.All
                            .Where(entry => IsAuthorizedTool(options, request.User, entry))
                            .Select(entry => new Tool
                            {
                                Name = entry.Name,
                                Description = entry.Description,
                                InputSchema = entry.InputSchema
                            })
                            .ToList()
                    };
                })

                ;

            if (options.EnableOfficialTasks)
            {
                var taskLedger = new WorkerTaskLedger();
                workerClient.JobStateChanged += taskLedger.ObserveJob;
                workerClient.GenerationEnded += taskLedger.ReleaseGeneration;
                var adapter = new OfficialTasksAdapter(taskLedger, workerClient, jobOwners,
                    credentialStore != null, ResolveTaskIdentity, HandleToolCallAsync);
                builder.Services.Configure<ModelContextProtocol.Server.McpServerOptions>(adapter.Configure);
            }
            else
            {
                mcpBuilder.WithCallToolHandler((request, cancellationToken) =>
                    new ValueTask<CallToolResult>(HandleToolCallAsync(request, cancellationToken)));
            }

            async Task<CallToolResult> HandleToolCallAsync(
                ModelContextProtocol.Server.RequestContext<CallToolRequestParams> request,
                CancellationToken cancellationToken)
            {
                    if (!StaticToolManifest.TryGet(request.Params.Name, out var requestedTool) ||
                        !IsAuthorizedTool(options, request.User, requestedTool))
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

                    var scoped = !string.IsNullOrWhiteSpace(options.ClientCredentialsFile);
                    var jobOperation = request.Params.Name is "zemax_job_status" or "zemax_job_list" or "zemax_job_cancel";
                    if (scoped && jobOperation && request.Params.Name != "zemax_job_list")
                    {
                        if (!JobOwnerRegistry.TryGetJobId(request.Params, out var requestedJobId) ||
                            !jobOwners.IsOwned(clientId, requestedJobId, workerClient.CurrentGeneration))
                            return JobOwnerRegistry.Denied();
                    }

                    // Scoped Job status/list/cancel are metadata operations:
                    // authorize by immutable creator identity and generation,
                    // not by the optical-system edit lease. Other clients can
                    // observe their own Jobs even while a foreign Job owns the
                    // optical system.
                    CallToolResult result;
                    if (scoped && jobOperation)
                    {
                        var requestedLimit = 50;
                        if (request.Params.Name == "zemax_job_list" &&
                            !JobOwnerRegistry.TryGetRequestedListLimit(request.Params, out requestedLimit))
                            return new CallToolResult
                            {
                                Content = new List<ContentBlock>
                                {
                                    new TextContentBlock { Text = "Job list limit must be an integer between 1 and 128." }
                                },
                                IsError = true
                            };

                        var workerRequest = request.Params.Name == "zemax_job_list"
                            ? JobOwnerRegistry.ExpandListRequest(request.Params)
                            : request.Params;
                        result = await workerClient.CallToolAsync(workerRequest, cancellationToken, progressHandler).ConfigureAwait(false);
                        if (request.Params.Name == "zemax_job_list")
                            result = JobOwnerRegistry.FilterList(result, clientId, workerClient.CurrentGeneration, jobOwners, requestedLimit);
                        else if (JobOwnerRegistry.TryGetJobId(request.Params, out var authorizedJobId))
                            result = JobOwnerRegistry.ValidateSingleResult(result, authorizedJobId);
                    }
                    else
                    {
                        using (await controlLease.AcquireAsync(clientId, request.Params.Name, cancellationToken).ConfigureAwait(false))
                        {
                            result = await workerClient.CallToolAsync(request.Params, cancellationToken, progressHandler).ConfigureAwait(false);
                            if (TryGetStartedJobId(request.Params.Name, result, out var jobId))
                            {
                                var generation = workerClient.CurrentGeneration;
                                if (scoped) jobOwners.Register(clientId, jobId, generation);
                                if (controlLease.RetainForJob(clientId, jobId, generation) &&
                                    workerClient.TryGetJobStatus(generation, jobId, out var latestJob) &&
                                    latestJob != null)
                                {
                                    controlLease.ObserveJob(generation, latestJob);
                                }
                            }
                        }
                    }

                    if (string.Equals(request.Params.Name, "zemax_disconnect", StringComparison.Ordinal) &&
                        IsSuccessfulDisconnect(result))
                        controlLease.ReleaseOwnership(clientId);

                    return result;
            }

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
                ClientCredentialStore.Credential? scopedCredential = null;
                if (context.Request.Path.StartsWithSegments(options.McpPath))
                {
                    if (credentialStore != null)
                    {
                        try
                        {
                            scopedCredential = credentialStore.Authenticate(context.Request.Headers.Authorization.ToString());
                        }
                        catch (Exception ex)
                        {
                            // Missing or malformed credential files revoke all
                            // access until repaired. Never fall back to legacy
                            // shared-token or unauthenticated local access.
                            Log.Error(ex, "Client credential file cannot be read; failing closed");
                            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                            return;
                        }
                        if (scopedCredential == null)
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            context.Response.Headers.WWWAuthenticate = "Bearer";
                            return;
                        }
                    }
                    else if (!HasValidToken(context, options.AccessToken))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        return;
                    }
                }

                if (credentialStore != null &&
                    (string.Equals(context.Request.Path.Value, options.McpPath + "/health", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(context.Request.Path.Value, options.McpPath + "/activity", StringComparison.OrdinalIgnoreCase)))
                {
                    // These legacy diagnostic endpoints carry all clients' Jobs,
                    // progress and lease identities. Never expose their full
                    // payload in scoped mode; return a safe liveness response.
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        bridgeRunning = true,
                        authenticationMode = "scoped",
                        clientId = scopedCredential!.Id,
                        permission = scopedCredential.Permission,
                        jobDiagnostics = "per-client job tools only"
                    }).ConfigureAwait(false);
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
                    new("zemax-mcp-auth-profile", scopedCredential != null
                        ? "scoped:" + scopedCredential.Id
                        : string.IsNullOrWhiteSpace(options.AccessToken) ? "local" : "shared-token"),
                    new("zemax-mcp-permission", scopedCredential?.Permission ?? "read-write"),
                    new("zemax-mcp-remote-endpoint", context.Connection.RemoteIpAddress?.ToString() ?? "local")
                };
                if (!string.IsNullOrWhiteSpace(instanceHeader)) claims.Add(new Claim("zemax-mcp-client-instance", instanceHeader));
                var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sessionId)) claims.Add(new Claim("zemax-mcp-session-id", HashIdentityComponent(sessionId)));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "zemax-mcp-token"));
                await next().ConfigureAwait(false);
            });

            // Activity must remain observable even when a ZOS-API call delays
            // the Worker's GetStatus RPC.
            app.MapGet(options.McpPath + "/activity", (HttpContext http) =>
            {
                var profile = http.User.FindFirst("zemax-mcp-auth-profile")?.Value;
                return Results.Json(credentialStore == null ? activity.GetHealth()
                    : activity.GetForClient(profile != null && profile.StartsWith("scoped:", StringComparison.Ordinal)
                        ? "token:" + profile : ""));
            });

            app.MapGet(options.McpPath + "/health", async (HttpContext httpContext, CancellationToken cancellationToken) =>
            {
                WorkerStatus? status = null;
                try { status = await worker.GetStatusAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warning(ex, "Worker health RPC failed"); }
                var profile = httpContext.User.FindFirst("zemax-mcp-auth-profile")?.Value;
                var activityHealth = credentialStore == null ? activity.GetHealth()
                    : activity.GetForClient(profile != null && profile.StartsWith("scoped:", StringComparison.Ordinal)
                        ? "token:" + profile : "");
                return Results.Json(new
                {
                    bridgeRunning = true,
                    mcpServerRunning = status != null,
                    hostVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    workerVersion = status?.WorkerVersion,
                    zosApiAssemblyVersion = status?.ZosApiAssemblyVersion,
                    zosApiFileVersion = status?.ZosApiFileVersion,
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
                    authenticationRequired = !string.IsNullOrWhiteSpace(options.AccessToken) || credentialStore != null,
                    originValidationEnabled = true,
                    readOnly = options.ReadOnly,
                    snapshotDirectory = status?.SnapshotDirectory ?? options.SnapshotDirectory,
                    lastSnapshotPath = status?.LastSnapshotPath,
                    jobs = credentialStore == null
                        ? status?.Jobs ?? Array.Empty<WorkerJobStatus>()
                        : (status?.Jobs ?? Array.Empty<WorkerJobStatus>())
                            .Where(job => {
                                var profile = httpContext.User.FindFirst("zemax-mcp-auth-profile")?.Value;
                                return profile != null && profile.StartsWith("scoped:", StringComparison.Ordinal) &&
                                    jobOwners.IsOwned("token:" + profile, job.JobId, worker.CurrentGeneration);
                            }).ToArray(),
                    requestTimeoutSeconds = options.RequestTimeoutSeconds,
                    requestWriteTimeoutSeconds = options.RequestWriteTimeoutSeconds,
                    hardRecoveryTimeoutSeconds = options.HardRecoveryTimeoutSeconds,
                    jobRecoveryTimeoutSeconds = options.JobRecoveryTimeoutSeconds,
                    cancellationWriteTimeoutSeconds = options.CancellationWriteTimeoutSeconds,
                    lastClient = activityHealth.LastClient,
                    lastTool = activityHealth.LastTool,
                    lastRequestAt = activityHealth.LastRequestAt,
                    activeRequests = activityHealth.ActiveRequests,
                    activeOperations = activityHealth.ActiveOperations.Select(operation => new
                    {
                        client = operation.Client,
                        tool = operation.Tool,
                        startedAt = operation.StartedAt,
                        elapsedSeconds = Math.Max(0, (long)(DateTimeOffset.UtcNow - operation.StartedAt).TotalSeconds)
                    }),
                    clients = activityHealth.LastRequestAt == null ? Array.Empty<object>() : new[] { new { name = activityHealth.LastClient, lastRequestAt = activityHealth.LastRequestAt, lastMethod = activityHealth.LastTool } },
                    worker = credentialStore == null ? (object)worker.GetHealth() : new
                    {
                        workerGeneration = worker.CurrentGeneration,
                        detailsRestricted = true
                    },
                    controlLease = credentialStore == null ? (object)controlLease.GetHealth() : new { ownershipRestricted = true },
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

    internal static bool TryGetStartedJobId(string toolName, CallToolResult result, out string jobId)
    {
        jobId = string.Empty;
        if (result.IsError == true ||
            toolName is "zemax_job_status" or "zemax_job_list" or "zemax_job_cancel" or
                        "zemax_multistart_status" or "zemax_multistart_stop")
            return false;

        foreach (var content in result.Content.OfType<TextContentBlock>())
        {
            if (string.IsNullOrWhiteSpace(content.Text)) continue;
            try
            {
                using var document = JsonDocument.Parse(content.Text);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (document.RootElement.TryGetProperty("success", out var success) &&
                    success.ValueKind == JsonValueKind.False)
                    return false;
                if (document.RootElement.TryGetProperty("jobId", out var id) &&
                    id.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(id.GetString()))
                {
                    jobId = id.GetString()!;
                    return true;
                }
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static bool IsSuccessfulDisconnect(CallToolResult result)
    {
        if (result.IsError == true) return false;
        foreach (var content in result.Content.OfType<TextContentBlock>())
        {
            if (string.IsNullOrWhiteSpace(content.Text)) continue;
            try
            {
                using var document = JsonDocument.Parse(content.Text);
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("success", out var success) &&
                    success.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return success.GetBoolean();
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static bool IsAuthorizedTool(HostOptions options, ClaimsPrincipal? principal, ToolManifestEntry entry)
    {
        // The legacy multistart status/stop tools expose one process-global
        // optimizer state without a Job ID. Scoped clients must use the
        // owner-bound zemax_job_status/zemax_job_cancel variants instead.
        if (!string.IsNullOrWhiteSpace(options.ClientCredentialsFile) &&
            entry.Name is "zemax_multistart_status" or "zemax_multistart_stop")
            return false;
        if (!string.IsNullOrWhiteSpace(options.ClientCredentialsFile) &&
            (principal?.FindFirst("zemax-mcp-auth-profile")?.Value?.StartsWith("scoped:", StringComparison.Ordinal) != true ||
             principal.FindFirst("zemax-mcp-permission")?.Value is not ("read-only" or "read-write")))
            return false;
        if (!StaticToolManifest.IsAllowed(options.Toolset, entry.Name, options.ReadOnly))
            return false;
        // The legacy global --read-only switch intentionally allows Caution
        // operations. Per-credential read-only is stricter: only actual ReadOnly
        // impact commands are admitted, including to tools/list.
        return !ClientCredentialStore.IsReadOnly(principal) ||
               string.Equals(entry.Impact, "ReadOnly", StringComparison.Ordinal);
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


    // Raw Tasks protocol handlers receive JsonRpcRequest rather than the typed
    // tool context. Reconstruct exactly the same ownership identity so a second
    // credential cannot access another client's Task, even if it copies metadata.
    private static string ResolveTaskIdentity(JsonRpcRequest request)
    {
        var principal = request.Context?.User;
        var profile = principal?.FindFirst("zemax-mcp-auth-profile")?.Value;
        if (!string.IsNullOrWhiteSpace(profile) &&
            profile is not ("shared-token" or "local"))
            return "token:" + profile;

        var info = request.Context?.ClientInfo;
        var name = string.IsNullOrWhiteSpace(info?.Name) ? "unknown" : info.Name.Trim();
        var version = string.IsNullOrWhiteSpace(info?.Version) ? "unknown" : info.Version.Trim();
        var endpoint = principal?.FindFirst("zemax-mcp-remote-endpoint")?.Value ?? "unknown";
        var meta = (request.Params as JsonObject)?["_meta"] as JsonObject;
        var instanceId = GetRequestClientInstanceId(meta)
            ?? principal?.FindFirst("zemax-mcp-client-instance")?.Value;
        if (!string.IsNullOrWhiteSpace(instanceId))
            return $"client:{name}@{version}|instance:{instanceId}|remote:{endpoint}";
        var sessionId = principal?.FindFirst("zemax-mcp-session-id")?.Value;
        if (!string.IsNullOrWhiteSpace(sessionId))
            return $"client:{name}@{version}|session:{sessionId}|remote:{endpoint}";
        return $"client:{name}@{version}|remote:{endpoint}";
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
