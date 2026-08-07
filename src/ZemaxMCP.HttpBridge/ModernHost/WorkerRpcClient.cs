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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ZemaxRpcEnvelope>> _pending = new(StringComparer.Ordinal);
    private readonly JsonSerializerOptions _jsonOptions = McpJsonUtilities.DefaultOptions;
    private Process? _worker;
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _pump;
    private DateTimeOffset? _startedAt;

    public WorkerRpcClient(HostOptions options) => _options = options;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning) return;
            CloseStaleConnection();
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
                _pipe = pipe;
                _reader = reader;
                _writer = writer;
                _worker = worker;
                _startedAt = DateTimeOffset.UtcNow;
                worker.EnableRaisingEvents = true;
                worker.Exited += (_, _) => Log.Warning("ZOS-API Worker exited; the Host will recreate it on the next MCP request.");
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
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid().ToString("N");
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var invocation = new ToolInvocationRequest
            {
                Command = request.Name,
                Arguments = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>(), _jsonOptions),
                ReadOnly = _options.ReadOnly,
                Toolset = _options.Toolset
            };
            return await SendAsync<CallToolResult>(ZemaxRpcProtocol.InvokeTool, invocation, operationId, cancellationToken).ConfigureAwait(false);
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
        transport = "versioned private named-pipe RPC"
    };

    public async ValueTask DisposeAsync()
    {
        CloseStaleConnection();
        if (_pump != null) { try { await _pump.ConfigureAwait(false); } catch { } }
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("The Worker RPC connection closed."));
        _executionGate.Dispose();
        _writeGate.Dispose();
        _startupGate.Dispose();
    }

    private async Task<T> SendAsync<T>(string kind, object payload, string operationId, CancellationToken cancellationToken)
    {
        var writer = _writer ?? throw new InvalidOperationException("Worker is not running.");
        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ZemaxRpcEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion)) throw new InvalidOperationException("Could not track the Worker RPC request.");
        try
        {
            using var cancellationRegistration = cancellationToken.Register(() => _ = SendCancellationAsync(operationId));
            var message = new ZemaxRpcEnvelope
            {
                Kind = kind,
                RequestId = requestId,
                OperationId = operationId,
                ClientId = "mcp-host",
                Payload = JsonSerializer.SerializeToElement(payload, _jsonOptions)
            };
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await writer.WriteLineAsync(JsonSerializer.Serialize(message, _jsonOptions)).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
            var response = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(response.Kind, ZemaxRpcProtocol.Error, StringComparison.Ordinal))
            {
                var error = response.Payload.Deserialize<ZemaxRpcError>(_jsonOptions);
                throw new InvalidOperationException(error?.Message ?? "Worker command failed.");
            }
            return response.Payload.Deserialize<T>(_jsonOptions) ?? throw new InvalidOperationException("Worker returned an empty response.");
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    private async Task SendCancellationAsync(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) || _writer == null) return;
        try
        {
            var message = new ZemaxRpcEnvelope
            {
                Kind = ZemaxRpcProtocol.CancelOperation,
                RequestId = Guid.NewGuid().ToString("N"),
                OperationId = operationId,
                Payload = JsonSerializer.SerializeToElement(new { })
            };
            await _writeGate.WaitAsync().ConfigureAwait(false);
            try { await _writer.WriteLineAsync(JsonSerializer.Serialize(message, _jsonOptions)).ConfigureAwait(false); }
            finally { _writeGate.Release(); }
        }
        catch (Exception ex) { Log.Warning(ex, "Could not forward cancellation to Worker RPC"); }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (_reader != null)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                var message = JsonSerializer.Deserialize<ZemaxRpcEnvelope>(line, _jsonOptions);
                if (message != null && !string.IsNullOrWhiteSpace(message.RequestId) && _pending.TryGetValue(message.RequestId, out var pending))
                    pending.TrySetResult(message);
            }
        }
        catch (Exception ex) { Log.Error(ex, "Worker RPC response pump failed"); }
        finally
        {
            foreach (var pending in _pending.Values) pending.TrySetException(new IOException("Worker RPC connection ended."));
        }
    }

    private bool IsRunning
    {
        get
        {
            try { return _writer != null && _worker != null && !_worker.HasExited; }
            catch { return false; }
        }
    }

    private void CloseStaleConnection()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _writer = null;
        _reader = null;
        _pipe = null;
        if (_worker != null)
        {
            try { if (!_worker.HasExited) _worker.Kill(); } catch { }
            _worker.Dispose();
            _worker = null;
        }
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("Worker RPC restarted."));
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
