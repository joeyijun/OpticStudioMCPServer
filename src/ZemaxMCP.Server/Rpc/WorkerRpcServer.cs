using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Rpc;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.Server.Rpc;

/// <summary>
/// Private command server for the ZOS-API process. MCP terminates at the Host;
/// this class accepts only ZemaxMCP.Rpc envelopes over a local named pipe.
/// </summary>
internal sealed class WorkerRpcServer
{
    private readonly IServiceProvider _services;
    private readonly WorkerToolRegistry _tools;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _eventSignal = new(0);
    private readonly ConcurrentQueue<ZemaxRpcEnvelope> _events = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _operations = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public WorkerRpcServer(IServiceProvider services)
    {
        _services = services;
        _tools = services.GetRequiredService<WorkerToolRegistry>();
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(input, Encoding.UTF8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        using var eventShutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var eventPump = PumpEventsAsync(writer, eventShutdown.Token);
        var jobs = _services.GetRequiredService<McpJobManager>();
        var session = _services.GetRequiredService<IZemaxSession>();
        Action<McpJobSnapshot> jobChanged = job => EnqueueProgress(ToWorkerJobStatus(job));
        Action<string> snapshotCreated = path => EnqueueSnapshot(path);
        jobs.JobChanged += jobChanged;
        session.SnapshotCreated += snapshotCreated;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) return;
                ZemaxRpcEnvelope? message;
                try { message = JsonSerializer.Deserialize<ZemaxRpcEnvelope>(line, _jsonOptions); }
                catch (JsonException ex)
                {
                    await WriteErrorAsync(writer, string.Empty, string.Empty, "invalid_message", ex.Message).ConfigureAwait(false);
                    continue;
                }

                if (message == null || message.Version != ZemaxRpcProtocol.Version || string.IsNullOrWhiteSpace(message.Kind))
                {
                    await WriteErrorAsync(writer, message?.RequestId ?? string.Empty, message?.OperationId ?? string.Empty,
                        "unsupported_protocol", "The Worker does not support this RPC message version.").ConfigureAwait(false);
                    continue;
                }

                // A cancellation must never wait for an active ZOS command.
                if (string.Equals(message.Kind, ZemaxRpcProtocol.CancelOperation, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(message.OperationId) && _operations.TryGetValue(message.OperationId, out var operation)) operation.Cancel();
                    await WriteResultAsync(writer, message.RequestId, message.OperationId, new { cancelled = true }).ConfigureAwait(false);
                    continue;
                }

                _ = ProcessAsync(message, writer, cancellationToken);
            }
        }
        finally
        {
            jobs.JobChanged -= jobChanged;
            session.SnapshotCreated -= snapshotCreated;
            eventShutdown.Cancel();
            try { _eventSignal.Release(); } catch { }
            try { await eventPump.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task ProcessAsync(ZemaxRpcEnvelope message, StreamWriter writer, CancellationToken shutdown)
    {
        try
        {
            switch (message.Kind)
            {
                case ZemaxRpcProtocol.GetStatus:
                    await WriteResultAsync(writer, message.RequestId, message.OperationId, CreateStatus()).ConfigureAwait(false);
                    return;
                case ZemaxRpcProtocol.InvokeTool:
                    await InvokeToolAsync(message, writer, shutdown).ConfigureAwait(false);
                    return;
                default:
                    await WriteErrorAsync(writer, message.RequestId, message.OperationId, "unsupported_command",
                        "Unsupported Worker RPC command: " + message.Kind).ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            await WriteErrorAsync(writer, message.RequestId, message.OperationId, "cancelled", "The OpticStudio operation was cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(writer, message.RequestId, message.OperationId, "worker_error", ex.Message).ConfigureAwait(false);
        }
    }

    private async Task InvokeToolAsync(ZemaxRpcEnvelope message, StreamWriter writer, CancellationToken shutdown)
    {
        var invocation = message.Payload.Deserialize<ToolInvocationRequest>(_jsonOptions)
            ?? throw new InvalidOperationException("Tool invocation payload is missing.");
        if (string.IsNullOrWhiteSpace(invocation.Command) || !_tools.Tools.ContainsKey(invocation.Command))
            throw new InvalidOperationException("Unknown OpticStudio tool: " + invocation.Command);
        if (!StaticToolManifest.IsAllowed(invocation.Toolset, invocation.Command, invocation.ReadOnly))
            throw new InvalidOperationException("The selected toolset/read-only policy does not permit " + invocation.Command + ".");

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        if (!_operations.TryAdd(message.OperationId, operation))
            throw new InvalidOperationException("An operation with this ID is already active.");
        try
        {
            await _executionGate.WaitAsync(operation.Token).ConfigureAwait(false);
            try
            {
                var result = await _tools.InvokeAsync(invocation.Command, invocation.Arguments, operation.Token).ConfigureAwait(false);
                await WriteResultAsync(writer, message.RequestId, message.OperationId, new
                {
                    content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, _jsonOptions) } },
                    isError = false
                }).ConfigureAwait(false);
            }
            finally { _executionGate.Release(); }
        }
        finally { _operations.TryRemove(message.OperationId, out _); }
    }

    private WorkerStatus CreateStatus()
    {
        var session = _services.GetRequiredService<IZemaxSession>();
        var jobs = _services.GetRequiredService<McpJobManager>();
        return new WorkerStatus
        {
            RpcVersion = ZemaxRpcProtocol.Version,
            ManifestFingerprint = StaticToolManifest.ContractFingerprint,
            ZosApiLoaded = true,
            Connected = session.IsConnected,
            ConnectionMode = session.CurrentMode?.ToString() ?? "not-connected",
            ZosApiAssembly = typeof(ZOSAPI.ZOSAPI_Connection).Assembly.Location,
            OpticStudioDataDirectory = session.ZemaxDataDir,
            CurrentLicenseStatus = session.CurrentLicenseStatus,
            LastLicenseStatus = session.LastLicenseStatus,
            LicenseValidForApi = session.LicenseValidForApi,
            LastConnectionError = session.LastConnectionError,
            SnapshotDirectory = session.SnapshotDirectory,
            LastSnapshotPath = session.LastSnapshotPath,
            Jobs = jobs.List().Select(ToWorkerJobStatus).ToArray()
        };
    }

    private static WorkerJobStatus ToWorkerJobStatus(McpJobSnapshot job) => new()
    {
        JobId = job.JobId,
        ToolName = job.ToolName,
        State = job.State.ToString(),
        Fraction = job.Progress,
        QueuePosition = job.QueuePosition,
        Message = job.Message
    };

    private void EnqueueProgress(WorkerJobStatus job)
    {
        _events.Enqueue(new ZemaxRpcEnvelope
        {
            Kind = ZemaxRpcProtocol.Progress,
            OperationId = job.JobId,
            Payload = JsonSerializer.SerializeToElement(new OperationProgress
            {
                OperationId = job.JobId,
                ToolName = job.ToolName,
                Fraction = job.Fraction,
                QueuePosition = job.QueuePosition,
                State = job.State,
                Message = job.Message
            }, _jsonOptions)
        });
        _eventSignal.Release();
    }

    private void EnqueueSnapshot(string path)
    {
        _events.Enqueue(new ZemaxRpcEnvelope
        {
            Kind = ZemaxRpcProtocol.SnapshotCreated,
            Payload = JsonSerializer.SerializeToElement(new SnapshotCreatedEvent { Path = path }, _jsonOptions)
        });
        _eventSignal.Release();
    }

    private async Task PumpEventsAsync(StreamWriter writer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _eventSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (_events.TryDequeue(out var message))
                await WriteAsync(writer, message).ConfigureAwait(false);
        }
    }

    private Task WriteResultAsync(StreamWriter writer, string requestId, string operationId, object result) =>
        WriteAsync(writer, new ZemaxRpcEnvelope
        {
            Kind = ZemaxRpcProtocol.Result,
            RequestId = requestId,
            OperationId = operationId,
            Payload = JsonSerializer.SerializeToElement(result, _jsonOptions)
        });

    private Task WriteErrorAsync(StreamWriter writer, string requestId, string operationId, string code, string message) =>
        WriteAsync(writer, new ZemaxRpcEnvelope
        {
            Kind = ZemaxRpcProtocol.Error,
            RequestId = requestId,
            OperationId = operationId,
            Payload = JsonSerializer.SerializeToElement(new ZemaxRpcError { Code = code, Message = message }, _jsonOptions)
        });

    private async Task WriteAsync(StreamWriter writer, ZemaxRpcEnvelope message)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try { await writer.WriteLineAsync(JsonSerializer.Serialize(message, _jsonOptions)).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }
}
