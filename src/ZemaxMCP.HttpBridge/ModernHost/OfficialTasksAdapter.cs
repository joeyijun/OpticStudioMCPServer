#pragma warning disable MCPEXP001, MCPEXP002, MCPEXP004
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ZemaxMCP.Rpc;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Optional, server-owned Tasks protocol adapter. It exposes only approved long
/// Worker Job tools and does not run or queue a second copy of any ZOS-API job.
/// The SDK's automatic WithTasks wrapper is intentionally not used.
/// </summary>
internal sealed class OfficialTasksAdapter
{
    private static readonly HashSet<string> EligibleTools = new(StringComparer.Ordinal)
    {
        "zemax_run_nsc_ray_trace", "zemax_run_tolerancing", "zemax_pop",
        "zemax_global_search", "zemax_multistart_optimize"
    };

    private readonly WorkerTaskLedger _ledger;
    private readonly WorkerRpcClient _worker;
    private readonly JobOwnerRegistry _owners;
    private readonly OpticStudioControlLease _controlLease;
    private readonly bool _scoped;
    private readonly Func<JsonRpcRequest, string> _getOwner;
    private readonly Func<RequestContext<CallToolRequestParams>, bool> _isAuthorized;
    private readonly Func<RequestContext<CallToolRequestParams>, CancellationToken, Task<CallToolResult>> _execute;
    private const string ExtensionId = "io.modelcontextprotocol/tasks";

    internal OfficialTasksAdapter(
        WorkerTaskLedger ledger, WorkerRpcClient worker, JobOwnerRegistry owners,
        OpticStudioControlLease controlLease, bool scoped, Func<JsonRpcRequest, string> getOwner,
        Func<RequestContext<CallToolRequestParams>, bool> isAuthorized,
        Func<RequestContext<CallToolRequestParams>, CancellationToken, Task<CallToolResult>> execute)
    {
        _ledger = ledger;
        _worker = worker;
        _owners = owners;
        _controlLease = controlLease;
        _scoped = scoped;
        _getOwner = getOwner;
        _isAuthorized = isAuthorized;
        _execute = execute;
    }

    internal void Configure(McpServerOptions options)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Extensions ??= new Dictionary<string, object>();
        options.Capabilities.Extensions[ExtensionId] = new JsonObject();
        options.Handlers.CallToolWithAlternateHandler = CallAsync;
        options.RequestHandlers ??= new List<McpServerRequestHandler>();
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = TasksProtocol.MethodTasksGet,
            RoutingNameParameter = "taskId",
            Handler = GetAsync
        });
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = TasksProtocol.MethodTasksUpdate,
            RoutingNameParameter = "taskId",
            Handler = UpdateAsync
        });
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = TasksProtocol.MethodTasksCancel,
            RoutingNameParameter = "taskId",
            Handler = CancelAsync
        });
    }

    private async ValueTask<ResultOrAlternate<CallToolResult>> CallAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        // All old clients, down-level clients and non-eligible tools keep their
        // ordinary tools/call result. Authorization happens inside _execute.
        var eligible = EligibleTools.Contains(request.Params.Name) && IsTaskNegotiated(request.JsonRpcRequest);
        // A full ledger must not reveal live workload/capacity to a credential
        // that is not permitted to invoke the tool. Run the ordinary Host
        // denial before checking shared Task capacity.
        if (eligible && !_isAuthorized(request))
            return new ResultOrAlternate<CallToolResult>(
                await _execute(request, cancellationToken).ConfigureAwait(false));
        using var admission = eligible ? _ledger.TryReserveAdmission() : null;
        if (eligible && admission == null)
            return new ResultOrAlternate<CallToolResult>(new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock { Text = "Official Tasks are at capacity. No Worker Job was started; retry after an active Task finishes." }
                }
            });
        var result = await _execute(request, cancellationToken).ConfigureAwait(false);
        if (!eligible || !Program.TryGetStartedJobId(request.Params.Name, result, out var jobId))
            return new ResultOrAlternate<CallToolResult>(result);

        var owner = _getOwner(request.JsonRpcRequest);
        var generation = _worker.CurrentGeneration;
        if (!_ledger.TryRegister(owner, jobId, generation, out var snapshot) || snapshot == null)
            return new ResultOrAlternate<CallToolResult>(new CallToolResult
            {
                IsError = true,
                Content = new List<ContentBlock>
                {
                    new TextContentBlock
                    {
                        Text = "Worker Job " + jobId + " started, but official Task registration failed. Use zemax_job_status with this Job ID; no Task ID was created."
                    }
                }
            });

        // A terminal Worker event may have arrived before registration.
        if (_worker.TryGetJobStatus(generation, jobId, out var current) && current != null)
            ObserveJob(generation, current);

        return ResultOrAlternate<CallToolResult>.FromAlternate(new CreateTaskResult
        {
            TaskId = snapshot.TaskId,
            Status = McpTaskStatus.Working,
            StatusMessage = snapshot.Message,
            CreatedAt = snapshot.CreatedAt,
            LastUpdatedAt = snapshot.UpdatedAt,
            PollIntervalMs = 1000
        }, McpTasksJsonContext.Default.CreateTaskResult);
    }

    /// <summary>
    /// Task polling and Task cancellation can see a terminal Worker result
    /// before the asynchronous Worker status event. Update the Task ledger
    /// AND the optical control lease from the same generation-bound evidence.
    /// In particular, an older cancelled Task must not block the next Job.
    /// </summary>
    private void ObserveJob(long generation, WorkerJobStatus job)
    {
        _ledger.ObserveJob(generation, job);
        _controlLease.ObserveJob(generation, job);
    }

    private async ValueTask<JsonNode?> GetAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        RequireTasks(request);
        var taskId = ReadTaskId(request);
        var owner = _getOwner(request);
        var snapshot = GetOwned(owner, taskId);
        if (snapshot.State == "working")
        {
            if (_worker.HasForegroundTool)
            {
                // A running COM-bound foreground RPC owns Worker admission.
                // Querying job_status here would line up behind that RPC and
                // could stall an otherwise healthy Tasks/get poll for minutes.
                // Use generation-matched progress already observed by the
                // Host instead; the next unblocked poll fetches the real result.
                if (_worker.TryGetJobStatus(snapshot.Generation, snapshot.JobId, out var observed) &&
                    observed != null)
                    ObserveJob(snapshot.Generation, observed);
            }
            else
            {
                await RefreshAsync(owner, snapshot, cancellationToken).ConfigureAwait(false);
            }
            snapshot = GetOwned(owner, taskId);
        }

        if (snapshot.ResultExpired)
            throw new McpProtocolException("The Task result has expired.", McpErrorCode.InvalidParams);

        GetTaskResult response = snapshot.State switch
        {
            "completed" when snapshot.Result != null => new CompletedTaskResult
            {
                TaskId = taskId, CreatedAt = snapshot.CreatedAt, LastUpdatedAt = snapshot.UpdatedAt,
                StatusMessage = snapshot.Message, PollIntervalMs = 1000,
                Result = JsonSerializer.SerializeToElement(snapshot.Result, McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>())
            },
            "failed" => new FailedTaskResult
            {
                TaskId = taskId, CreatedAt = snapshot.CreatedAt, LastUpdatedAt = snapshot.UpdatedAt,
                StatusMessage = snapshot.Message, PollIntervalMs = 1000,
                Error = JsonSerializer.SerializeToElement(new JsonRpcErrorDetail
                {
                    Code = (int)McpErrorCode.InternalError, Message = snapshot.Message
                }, McpJsonUtilities.DefaultOptions.GetTypeInfo<JsonRpcErrorDetail>())
            },
            "cancelled" => new CancelledTaskResult
            {
                TaskId = taskId, CreatedAt = snapshot.CreatedAt, LastUpdatedAt = snapshot.UpdatedAt,
                StatusMessage = snapshot.Message, PollIntervalMs = 1000
            },
            _ => new WorkingTaskResult
            {
                TaskId = taskId, CreatedAt = snapshot.CreatedAt, LastUpdatedAt = snapshot.UpdatedAt,
                StatusMessage = snapshot.Message, PollIntervalMs = 1000
            }
        };
        response.ResultType = "complete";
        return JsonSerializer.SerializeToNode(response, McpTasksJsonContext.Default.GetTaskResult);
    }

    private ValueTask<JsonNode?> UpdateAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        RequireTasks(request);
        var owner = _getOwner(request);
        _ = GetOwned(owner, ReadTaskId(request)); // always authorize before examining input
        // Worker Jobs never initiate sampling/elicitation via MCP Tasks.
        // Acknowledging arbitrary responses would misleadingly claim delivery.
        if (request.Params?["inputResponses"] is JsonObject responses && responses.Count == 0 ||
            request.Params?["inputResponses"] == null)
            return ValueTask.FromResult<JsonNode?>(
                JsonSerializer.SerializeToNode(new UpdateTaskResult(), McpTasksJsonContext.Default.UpdateTaskResult));

        throw new McpProtocolException("This Worker Job has no outstanding Task input requests.", McpErrorCode.InvalidParams);
    }

    private async ValueTask<JsonNode?> CancelAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        RequireTasks(request);
        var owner = _getOwner(request);
        var snapshot = GetOwned(owner, ReadTaskId(request));
        if (_ledger.TryRequestCancel(owner, snapshot.TaskId, _worker.CurrentGeneration, out var jobId))
        {
            // Existing Worker Job cancellation path, never a second COM cancel
            // mechanism. Validate returned Job ID before acknowledging.
            var result = await _worker.CallToolAsync(new CallToolRequestParams
            {
                Name = "zemax_job_cancel",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["jobId"] = JsonSerializer.SerializeToElement(jobId)
                }
            }, cancellationToken).ConfigureAwait(false);

            if (result.IsError == true ||
                JobOwnerRegistry.ValidateSingleResult(result, jobId).IsError == true)
                throw new McpProtocolException("Worker Job cancellation was not acknowledged.", McpErrorCode.InternalError);

            if (TryReadStatus(result, jobId, out var state, out _, out _, out _))
                ObserveJob(snapshot.Generation, new WorkerJobStatus { JobId = jobId, State = state });
        }
        return JsonSerializer.SerializeToNode(new CancelTaskResult(), McpTasksJsonContext.Default.CancelTaskResult);
    }

    private async Task RefreshAsync(string owner, WorkerTaskSnapshot task, CancellationToken cancellationToken)
    {
        if (_worker.CurrentGeneration != task.Generation)
        {
            _ledger.ReleaseGeneration(task.Generation);
            return;
        }
        if (_scoped && !_owners.IsOwned(owner, task.JobId, task.Generation))
        {
            _ledger.FailUnavailableResult(owner, task.TaskId, task.Generation,
                "The Task no longer has a valid Worker Job ownership record.");
            return;
        }

        var result = await _worker.CallToolAsync(new CallToolRequestParams
        {
            Name = "zemax_job_status",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["jobId"] = JsonSerializer.SerializeToElement(task.JobId)
            }
        }, cancellationToken).ConfigureAwait(false);

        if (result.IsError == true ||
            !TryReadStatus(result, task.JobId, out var state, out var value, out var expired, out var message))
        {
            _ledger.FailUnavailableResult(owner, task.TaskId, task.Generation,
                "The Worker Job status or final payload is unavailable or invalid.");
            return;
        }
        ObserveJob(task.Generation, new WorkerJobStatus
        {
            JobId = task.JobId, State = state, Message = message
        });
        if (state.Equals("Completed", StringComparison.OrdinalIgnoreCase))
        {
            if (expired || value == null || value.Value.ValueKind == JsonValueKind.Undefined)
            {
                _ledger.FailUnavailableResult(owner, task.TaskId, task.Generation,
                    expired ? "The Worker Job result has expired." : "The completed Worker Job contains no final result.");
                return;
            }
            // Do not keep unlimited result blobs in the Host. Report an honest
            // unavailable result if a Worker exceeded the supported response size.
            var json = value.Value.GetRawText();
            if (json.Length > 4 * 1024 * 1024)
            {
                _ledger.FailUnavailableResult(owner, task.TaskId, task.Generation,
                    "The Worker Job result exceeds the Task result retention limit.");
                return;
            }
            var actual = new CallToolResult
            {
                Content = new List<ContentBlock> { new TextContentBlock { Text = json } },
                IsError = false
            };
            _ledger.TryComplete(owner, task.TaskId, task.JobId, task.Generation, actual);
        }
    }

    internal static bool TryReadStatus(CallToolResult result, string jobId, out string state,
        out JsonElement? value, out bool expired, out string? message)
    {
        state = string.Empty;
        value = null;
        expired = false;
        message = null;
        if (result.IsError == true || result.Content.Count != 1 ||
            result.Content[0] is not TextContentBlock text) return false;
        try
        {
            using var doc = JsonDocument.Parse(text.Text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryProperty(root, "jobId", out var id) || id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), jobId, StringComparison.Ordinal) ||
                !TryProperty(root, "state", out var st) || st.ValueKind != JsonValueKind.String)
                return false;
            state = st.GetString() ?? string.Empty;
            if (state is not ("Queued" or "Running" or "Cancelling" or "Completed" or "Cancelled" or "Failed"))
                return false;
            if (TryProperty(root, "resultExpired", out var x))
            {
                if (x.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
                expired = x.GetBoolean();
            }
            if (TryProperty(root, "result", out var data)) value = data.Clone();
            if (TryProperty(root, "message", out var msg) && msg.ValueKind == JsonValueKind.String)
                message = msg.GetString();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        value = default;
        return false;
    }

    private WorkerTaskSnapshot GetOwned(string owner, string taskId)
    {
        if (!_ledger.TryGet(owner, taskId, out var snapshot) || snapshot == null)
            throw new McpProtocolException("Task not found.", McpErrorCode.InvalidParams);
        return snapshot;
    }

    private static string ReadTaskId(JsonRpcRequest request)
    {
        if (request.Params is not JsonObject obj ||
            obj["taskId"] is not JsonValue value ||
            !value.TryGetValue<string>(out var taskId) ||
            string.IsNullOrWhiteSpace(taskId) || taskId.Length > 128)
            throw new McpProtocolException("Missing or invalid taskId.", McpErrorCode.InvalidParams);
        return taskId;
    }

    private static bool IsTaskNegotiated(JsonRpcRequest request) =>
        string.Equals(request.Context?.ProtocolVersion, "2026-07-28", StringComparison.Ordinal) &&
        (request.Context?.ClientCapabilities?.Extensions?.ContainsKey(ExtensionId) == true ||
         request.Params?["_meta"]?["io.modelcontextprotocol/clientCapabilities"]?["extensions"] is JsonObject extensions &&
         extensions.ContainsKey(ExtensionId));

    private static void RequireTasks(JsonRpcRequest request)
    {
        if (!string.Equals(request.Context?.ProtocolVersion, "2026-07-28", StringComparison.Ordinal))
            throw new McpProtocolException("Tasks require MCP protocol 2026-07-28.", McpErrorCode.MethodNotFound);
        if (!IsTaskNegotiated(request))
            throw new McpProtocolException("The Tasks client extension capability is required.", McpErrorCode.InvalidRequest);
    }
}
