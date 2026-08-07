using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Serilog;
using ZemaxMCP.Rpc;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Typed private RPC client.  The Host owns MCP and HTTP; the Worker receives
/// only versioned OpticStudio commands through its private named pipe.
/// </summary>
internal sealed class WorkerRpcClient : IAsyncDisposable
{
    private readonly HostOptions _options;
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private readonly object _connectionGate = new();
    private readonly object _recoveryGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ZemaxRpcEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = McpJsonUtilities.DefaultOptions;
    private Process? _worker;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _pump;
    private DateTimeOffset? _startedAt;
    private bool _disposed;
    private Task? _cancelledOperationRecovery;
    private OperationProgress? _lastProgress;
    private string? _lastSnapshotPath;

    public WorkerRpcClient(HostOptions options) => _options = options;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            CloseStaleConnection("Worker RPC connection is being replaced.");
            if (!File.Exists(_options.WorkerPath))
                throw new FileNotFoundException("Worker executable was not found.", _options.WorkerPath);

            var pipeName = "ZemaxMcpWorker-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pipe = CreatePrivateWorkerPipe(pipeName);
            var worker = StartWorker(pipeName, secret);
            try
            {
                await WaitForConnectionAsync(pipe, worker, cancellationToken).ConfigureAwait(false);
                var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                await AuthenticateAsync(reader, writer, worker, secret, cancellationToken).ConfigureAwait(false);
                lock (_connectionGate)
                {
                    _pipe = pipe;
                    _reader = reader;
                    _writer = writer;
                    _worker = worker;
                    _startedAt = DateTimeOffset.UtcNow;
                }
                worker.EnableRaisingEvents = true;
                worker.Exited += (_, _) => FaultWorkerConnection(new IOException("The ZOS-API Worker process exited."), expectedWorker: worker);
                _pump = Task.Run(PumpAsync);
            }
            catch
            {
                pipe.Dispose();
                try { if (!worker.HasExited) worker.Kill(); } catch { }
                worker.Dispose();
                throw;
            }
        }
        finally { _startupGate.Release(); }
    }

    public async Task<ListToolsResult> ListToolsAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return await SendAsync<ListToolsResult>(ZemaxRpcProtocol.GetToolCatalog,
            new ToolCatalogRequest { Toolset = _options.Toolset, ReadOnly = _options.ReadOnly }, Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
    }

    public async Task<CallToolResult> CallToolAsync(CallToolRequestParams request, CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid().ToString("N");
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The execution gate must be acquired before checking recovery. A
            // request that queued behind a soon-to-be-cancelled operation will
            // therefore observe that operation's recovery barrier after it
            // acquires the gate instead of entering the draining generation.
            await WaitForCancelledOperationRecoveryAsync(cancellationToken).ConfigureAwait(false);
            await StartAsync(cancellationToken).ConfigureAwait(false);
            var invocation = new ToolInvocationRequest
            {
                Command = request.Name,
                Arguments = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>(), _jsonOptions),
                ReadOnly = _options.ReadOnly,
                Toolset = _options.Toolset
            };
            return await SendAsync<CallToolResult>(ZemaxRpcProtocol.InvokeTool, invocation, operationId, cancellationToken,
                ownsGenerationRecovery: true).ConfigureAwait(false);
        }
        finally { _executionGate.Release(); }
    }

    public async Task<WorkerStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return await SendAsync<WorkerStatus>(ZemaxRpcProtocol.GetStatus, new { }, Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false);
    }

    public object GetHealth() => new
    {
        mcpServerRunning = _writer != null && _worker is { HasExited: false },
        workerPid = _worker?.Id,
        workerStartedAt = _startedAt,
        transport = "versioned private named-pipe RPC",
        lastProgress = _lastProgress,
        lastSnapshotPath = _lastSnapshotPath
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        var pump = _pump;
        CloseStaleConnection("The Worker RPC client is shutting down.");
        if (pump != null) { try { await pump.ConfigureAwait(false); } catch { } }
        _executionGate.Dispose();
        _writeGate.Dispose();
        _startupGate.Dispose();
    }

    private async Task<T> SendAsync<T>(string kind, object payload, string operationId, CancellationToken cancellationToken,
        bool ownsGenerationRecovery = false)
    {
        var writer = GetWriter() ?? throw new InvalidOperationException("Worker is not running.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ZemaxRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("Could not track the Worker RPC request.");
        var written = false;
        var backgroundRecoveryOwnsPending = false;
        try
        {
            var message = new ZemaxRpcEnvelope
            {
                Kind = kind,
                RequestId = requestId,
                OperationId = operationId,
                ClientId = "mcp-host",
                Payload = JsonSerializer.SerializeToElement(payload, _jsonOptions)
            };
            await WriteAsync(writer, message, cancellationToken,
                TimeSpan.FromSeconds(_options.RequestWriteTimeoutSeconds)).ConfigureAwait(false);
            written = true;
            var response = await WaitForResponseAsync(completion.Task, operationId, writer, cancellationToken).ConfigureAwait(false);
            if (string.Equals(response.Kind, ZemaxRpcProtocol.Error, StringComparison.Ordinal))
            {
                var error = response.Payload.Deserialize<ZemaxRpcError>(_jsonOptions);
                throw new InvalidOperationException(error?.Message ?? "Worker command failed.");
            }
            return response.Payload.Deserialize<T>(_jsonOptions) ?? throw new InvalidOperationException("Worker returned an empty response.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && written && ownsGenerationRecovery)
        {
            backgroundRecoveryOwnsPending = true;
            BeginCancelledOperationRecovery(completion.Task, requestId, operationId, writer);
            throw;
        }
        finally
        {
            if (!backgroundRecoveryOwnsPending) _pending.TryRemove(requestId, out _);
        }
    }

    private async Task SendCancellationAsync(string operationId)
    {
        var writer = GetWriter();
        if (string.IsNullOrWhiteSpace(operationId) || writer == null) return;
        try
        {
            var message = new ZemaxRpcEnvelope
            {
                Kind = ZemaxRpcProtocol.CancelOperation,
                RequestId = Guid.NewGuid().ToString("N"),
                OperationId = operationId,
                Payload = JsonSerializer.SerializeToElement(new { })
            };
            await WriteAsync(writer, message, CancellationToken.None,
                TimeSpan.FromSeconds(_options.CancellationWriteTimeoutSeconds)).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            FaultWorkerConnection(ex, expectedWriter: writer);
            Log.Warning(ex, "Timed out forwarding cancellation to Worker RPC");
        }
        catch (Exception ex) { Log.Warning(ex, "Could not forward cancellation to Worker RPC"); }
    }

    private async Task PumpAsync()
    {
        StreamReader? reader;
        lock (_connectionGate) reader = _reader;
        if (reader == null) return;
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) throw new EndOfStreamException("The Worker private pipe closed its response stream.");
                var message = JsonSerializer.Deserialize<ZemaxRpcEnvelope>(line, _jsonOptions)
                    ?? throw new InvalidDataException("The Worker returned an empty RPC envelope.");
                switch (message.Kind)
                {
                    case ZemaxRpcProtocol.Progress:
                        _lastProgress = message.Payload.Deserialize<OperationProgress>(_jsonOptions)
                            ?? throw new InvalidDataException("The Worker returned an invalid progress event.");
                        break;
                    case ZemaxRpcProtocol.SnapshotCreated:
                        _lastSnapshotPath = message.Payload.Deserialize<SnapshotCreatedEvent>(_jsonOptions)?.Path
                            ?? throw new InvalidDataException("The Worker returned an invalid snapshot event.");
                        break;
                    case ZemaxRpcProtocol.Result:
                    case ZemaxRpcProtocol.Error:
                        if (string.IsNullOrWhiteSpace(message.RequestId))
                            throw new InvalidDataException("The Worker returned a result without a request ID.");
                        if (_pending.TryGetValue(message.RequestId, out var pending)) pending.TrySetResult(message);
                        else Log.Debug("Ignoring uncorrelated Worker RPC response {RequestId}", message.RequestId);
                        break;
                    default:
                        throw new InvalidDataException("The Worker returned an unsupported RPC message kind: " + message.Kind);
                }
            }
        }
        catch (Exception ex) { FaultWorkerConnection(ex, expectedReader: reader); }
    }

    private bool IsRunning
    {
        get
        {
            try
            {
                lock (_connectionGate) return _writer != null && _worker != null && !_worker.HasExited;
            }
            catch { return false; }
        }
    }

    private StreamWriter? GetWriter()
    {
        lock (_connectionGate) return _writer;
    }

    private async Task WriteAsync(StreamWriter writer, ZemaxRpcEnvelope message, CancellationToken cancellationToken, TimeSpan? writeTimeout = null)
    {
        var lockTaken = false;
        var started = Stopwatch.GetTimestamp();
        CancellationTokenSource? deadline = null;
        try
        {
            var lockToken = cancellationToken;
            if (writeTimeout.HasValue)
            {
                deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(writeTimeout.Value);
                lockToken = deadline.Token;
            }
            try
            {
                await _writeGate.WaitAsync(lockToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline?.IsCancellationRequested == true)
            {
                var timeout = new TimeoutException("The Worker RPC pipe did not accept a message before its write deadline.");
                FaultWorkerConnection(timeout, expectedWriter: writer);
                throw timeout;
            }
            lockTaken = true;
            var write = writer.WriteLineAsync(JsonSerializer.Serialize(message, _jsonOptions));
            if (writeTimeout.HasValue)
            {
                var elapsed = Stopwatch.GetElapsedTime(started);
                var remaining = writeTimeout.Value - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    var timeout = new TimeoutException("The Worker RPC pipe did not accept a message before its write deadline.");
                    FaultWorkerConnection(timeout, expectedWriter: writer);
                    throw timeout;
                }
                var completed = await Task.WhenAny(write, Task.Delay(remaining)).ConfigureAwait(false);
                if (completed != write)
                {
                    var timeout = new TimeoutException("The Worker RPC pipe did not accept a message before its write deadline.");
                    FaultWorkerConnection(timeout, expectedWriter: writer);
                    throw timeout;
                }
            }
            await write.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            FaultWorkerConnection(ex, expectedWriter: writer);
            throw;
        }
        finally
        {
            if (lockTaken) _writeGate.Release();
            deadline?.Dispose();
        }
    }

    private async Task<ZemaxRpcEnvelope> WaitForResponseAsync(Task<ZemaxRpcEnvelope> responseTask, string operationId, StreamWriter writer, CancellationToken cancellationToken)
    {
        var callerCancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var softTimeout = Task.Delay(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
        var first = await Task.WhenAny(responseTask, callerCancellation, softTimeout).ConfigureAwait(false);
        if (first == responseTask) return await responseTask.ConfigureAwait(false);
        if (first == callerCancellation)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        Log.Warning("Worker operation {OperationId} exceeded the {Timeout}s soft timeout; requesting cancellation.", operationId, _options.RequestTimeoutSeconds);
        var recoveryDelay = TimeSpan.FromSeconds(_options.HardRecoveryTimeoutSeconds - _options.RequestTimeoutSeconds);
        var hardDeadline = Task.Delay(recoveryDelay);
        var cancellationDelivery = SendCancellationAsync(operationId);
        var afterSoftTimeout = await Task.WhenAny(responseTask, hardDeadline, cancellationDelivery).ConfigureAwait(false);
        if (afterSoftTimeout == responseTask) return await responseTask.ConfigureAwait(false);
        if (afterSoftTimeout == cancellationDelivery)
        {
            var afterCancellation = await Task.WhenAny(responseTask, hardDeadline).ConfigureAwait(false);
            if (afterCancellation == responseTask) return await responseTask.ConfigureAwait(false);
        }

        var timeout = new TimeoutException("The Worker did not finish after the soft timeout and cancellation grace period.");
        FaultWorkerConnection(timeout, expectedWriter: writer);
        throw timeout;
    }

    private async Task RecoverCancelledOperationAsync(Task<ZemaxRpcEnvelope> responseTask, string requestId, string operationId, StreamWriter writer)
    {
        try
        {
            // The HTTP request has already ended, but the private Worker must
            // still drain or be restarted so a cancelled COM call cannot leave
            // the next client permanently blocked.
            var deadline = Task.Delay(TimeSpan.FromSeconds(_options.HardRecoveryTimeoutSeconds - _options.RequestTimeoutSeconds));
            var cancellation = SendCancellationAsync(operationId);
            var completed = await Task.WhenAny(responseTask, deadline, cancellation).ConfigureAwait(false);
            if (completed == responseTask) return;
            if (completed == cancellation && await Task.WhenAny(responseTask, deadline).ConfigureAwait(false) == responseTask) return;
            FaultWorkerConnection(new TimeoutException("A client-cancelled Worker operation did not drain before the recovery deadline."), expectedWriter: writer);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Background recovery for cancelled Worker operation {OperationId} failed", operationId);
            FaultWorkerConnection(ex, expectedWriter: writer);
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private void BeginCancelledOperationRecovery(Task<ZemaxRpcEnvelope> responseTask, string requestId, string operationId, StreamWriter writer)
    {
        lock (_recoveryGate)
        {
            if (_cancelledOperationRecovery is { IsCompleted: false })
                throw new InvalidOperationException("A cancelled Worker operation recovery is already active.");
            _cancelledOperationRecovery = RecoverCancelledOperationAsync(responseTask, requestId, operationId, writer);
        }
    }

    private async Task WaitForCancelledOperationRecoveryAsync(CancellationToken cancellationToken)
    {
        Task? recovery;
        lock (_recoveryGate) recovery = _cancelledOperationRecovery;
        if (recovery == null) return;
        try { await recovery.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (recovery.IsCompleted)
            {
                lock (_recoveryGate)
                    if (ReferenceEquals(_cancelledOperationRecovery, recovery)) _cancelledOperationRecovery = null;
            }
        }
    }

    private void CloseStaleConnection(string reason) => FaultWorkerConnection(new IOException(reason));

    /// <summary>
    /// One recovery path for EOF, read/write failures, malformed frames and a
    /// hard timeout. The next request creates a clean Worker generation.
    /// </summary>
    private void FaultWorkerConnection(Exception reason, Process? expectedWorker = null, StreamReader? expectedReader = null, StreamWriter? expectedWriter = null)
    {
        StreamWriter? writer;
        StreamReader? reader;
        NamedPipeServerStream? pipe;
        Process? worker;
        lock (_connectionGate)
        {
            if ((expectedWorker != null && !ReferenceEquals(expectedWorker, _worker)) ||
                (expectedReader != null && !ReferenceEquals(expectedReader, _reader)) ||
                (expectedWriter != null && !ReferenceEquals(expectedWriter, _writer)))
                return;
            writer = _writer;
            reader = _reader;
            pipe = _pipe;
            worker = _worker;
            if (writer == null && reader == null && pipe == null && worker == null) return;
            _writer = null;
            _reader = null;
            _pipe = null;
            _worker = null;
            _startedAt = null;
        }

        if (!_disposed) Log.Error(reason, "Worker RPC generation faulted; pending calls fail and the next request will start a new Worker.");
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("The Worker RPC connection faulted.", reason));
        try { writer?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        if (worker != null)
        {
            try { if (!worker.HasExited) worker.Kill(); } catch { }
            try { worker.Dispose(); } catch { }
        }
    }

    private Process StartWorker(string pipeName, string secret)
    {
        var info = new ProcessStartInfo(_options.WorkerPath, "--pipe \"" + pipeName + "\"")
        {
            WorkingDirectory = Path.GetDirectoryName(_options.WorkerPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        info.Environment.Remove("ZEMAX_MCP_TOKEN");
        if (!string.IsNullOrWhiteSpace(_options.ZemaxRoot)) info.Environment["ZEMAX_ROOT"] = _options.ZemaxRoot;
        info.Environment["ZEMAX_MCP_PIPE_SECRET"] = secret;
        info.Environment["ZEMAX_MCP_READ_ONLY"] = _options.ReadOnly ? "1" : "0";
        info.Environment["ZEMAX_MCP_TOOLSET"] = _options.Toolset;
        info.Environment["ZEMAX_MCP_SNAPSHOT_DIR"] = _options.SnapshotDirectory;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Unable to launch the ZOS-API Worker.");
        process.ErrorDataReceived += (_, eventArgs) => { if (!string.IsNullOrWhiteSpace(eventArgs.Data)) Log.Information("Worker: {Message}", eventArgs.Data); };
        process.BeginErrorReadLine();
        return process;
    }

    private static NamedPipeServerStream CreatePrivateWorkerPipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Could not determine the current Windows user for the private Worker pipe.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task WaitForConnectionAsync(NamedPipeServerStream pipe, Process worker, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.WorkerStartupTimeoutSeconds));
        var connection = pipe.WaitForConnectionAsync(timeout.Token);
        while (!connection.IsCompleted)
        {
            if (worker.HasExited)
                throw new InvalidOperationException("The Worker exited before the private pipe connected. Exit code: " + worker.ExitCode + ".");
            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }
        await connection.ConfigureAwait(false);
    }

    private async Task AuthenticateAsync(StreamReader reader, StreamWriter writer, Process worker, string secret, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.WorkerStartupTimeoutSeconds));
        var hello = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        var expected = "ZEMAX_MCP_PIPE_HELLO|" + worker.Id + "|" + secret;
        if (!string.Equals(hello, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The Worker private pipe handshake was rejected.");
        await writer.WriteLineAsync("ZEMAX_MCP_PIPE_OK").ConfigureAwait(false);
    }
}
