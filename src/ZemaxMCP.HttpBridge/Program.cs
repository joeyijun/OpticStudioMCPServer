using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;
using ZemaxMCP.Toolsets;

namespace ZemaxMCP.HttpBridge;

/// <summary>
/// A Windows-only, stateful Streamable HTTP MCP Host. It removes the Node.js /
/// supergateway dependency and keeps ZOS-API isolated in a private Worker process.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var options = BridgeOptions.Parse(args);
        if (!File.Exists(options.ServerPath))
        {
            Console.Error.WriteLine("Server executable was not found: " + options.ServerPath);
            return 2;
        }

        Directory.CreateDirectory(options.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(options.LogDirectory, "http-bridge-.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            using (var bridge = new StdioMcpBridge(options))
            {
                bridge.RunAsync().GetAwaiter().GetResult();
            }
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "HTTP bridge terminated unexpectedly");
            return 1;
        }
        finally { Log.CloseAndFlush(); }
    }
}

internal sealed class BridgeOptions
{
    private const int MaximumActiveMcpClients = 20;
    public string ServerPath { get; private set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "ZemaxMCP.Worker.exe");
    public string ZemaxRoot { get; private set; } = "";
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 8000;
    public string Path { get; private set; } = "/mcp/";
    public string LogDirectory { get; private set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
    public int RequestTimeoutSeconds { get; private set; } = 300;
    public int HardRecoveryTimeoutSeconds { get; private set; } = 360;
    public int WorkerStartupTimeoutSeconds { get; private set; } = 90;
    public int MaxActiveMcpClients { get; private set; } = 1;
    public int MaxQueuedRequests { get; private set; } = 16;
    public string AccessToken { get; private set; } = Environment.GetEnvironmentVariable("ZEMAX_MCP_TOKEN") ?? "";
    public bool ReadOnly { get; private set; }
    // Development-only compatibility transport used by the protocol fixture.
    // Production launches always isolate ZOS-API behind a named-pipe Worker.
    public bool UseStdioBackend { get; private set; }
    internal bool TestFailSseOpen { get; private set; }
    internal bool TestFailInitializeResponseWrite { get; private set; }
    public string ToolsetProfile { get; private set; } = ToolsetPolicy.FullExpert;
    public string SnapshotDirectory { get; private set; } = Environment.GetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR") ??
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "snapshots");

    public static BridgeOptions Parse(string[] args)
    {
        var result = new BridgeOptions();
        if (args.Length % 2 != 0) throw new ArgumentException("Bridge arguments must be supplied as --option value pairs.");
        for (var i = 0; i < args.Length; i += 2)
        {
            var value = args[i + 1];
            switch (args[i].ToLowerInvariant())
            {
                case "--server": result.ServerPath = value; break;
                case "--zemax-root": result.ZemaxRoot = value; break;
                case "--host": result.Host = value; break;
                case "--port":
                    if (!int.TryParse(value, out var port) || port < 1 || port > 65535)
                        throw new ArgumentException("--port must be a number from 1 to 65535.");
                    result.Port = port;
                    break;
                case "--path": result.Path = value.TrimEnd('/') + "/"; break;
                case "--log-dir": result.LogDirectory = value; break;
                case "--request-timeout-seconds":
                    if (!int.TryParse(value, out var timeout) || timeout < 10 || timeout > 3600)
                        throw new ArgumentException("--request-timeout-seconds must be between 10 and 3600.");
                    result.RequestTimeoutSeconds = timeout;
                    break;
                case "--hard-recovery-timeout-seconds":
                    if (!int.TryParse(value, out var hardTimeout) || hardTimeout < 20 || hardTimeout > 7200)
                        throw new ArgumentException("--hard-recovery-timeout-seconds must be between 20 and 7200.");
                    result.HardRecoveryTimeoutSeconds = hardTimeout;
                    break;
                case "--worker-startup-timeout-seconds":
                    if (!int.TryParse(value, out var workerStartupTimeout) || workerStartupTimeout < 10 || workerStartupTimeout > 600)
                        throw new ArgumentException("--worker-startup-timeout-seconds must be between 10 and 600.");
                    result.WorkerStartupTimeoutSeconds = workerStartupTimeout;
                    break;
                case "--max-active-mcp-clients":
                    if (!int.TryParse(value, out var maxClients) || maxClients < 1 || maxClients > MaximumActiveMcpClients)
                        throw new ArgumentException("--max-active-mcp-clients must be between 1 and " + MaximumActiveMcpClients + ".");
                    result.MaxActiveMcpClients = maxClients;
                    break;
                case "--max-queued-requests":
                    if (!int.TryParse(value, out var maxQueuedRequests) || maxQueuedRequests < 0 || maxQueuedRequests > 100)
                        throw new ArgumentException("--max-queued-requests must be between 0 and 100.");
                    result.MaxQueuedRequests = maxQueuedRequests;
                    break;
                case "--read-only":
                    if (!bool.TryParse(value, out var readOnly)) throw new ArgumentException("--read-only must be true or false.");
                    result.ReadOnly = readOnly;
                    break;
                case "--stdio-backend":
                    if (!bool.TryParse(value, out var stdioBackend)) throw new ArgumentException("--stdio-backend must be true or false.");
                    result.UseStdioBackend = stdioBackend;
                    break;
                case "--test-fail-sse-open":
                    if (!bool.TryParse(value, out var failSseOpen)) throw new ArgumentException("--test-fail-sse-open must be true or false.");
                    result.TestFailSseOpen = failSseOpen;
                    break;
                case "--test-fail-initialize-response-write":
                    if (!bool.TryParse(value, out var failInitializeWrite)) throw new ArgumentException("--test-fail-initialize-response-write must be true or false.");
                    result.TestFailInitializeResponseWrite = failInitializeWrite;
                    break;
                case "--snapshot-dir": result.SnapshotDirectory = value; break;
                case "--toolset": result.ToolsetProfile = ToolsetPolicy.NormalizeProfile(value); break;
                default: throw new ArgumentException("Unknown bridge option: " + args[i]);
            }
        }
        if (string.IsNullOrWhiteSpace(result.ServerPath)) throw new ArgumentException("--server cannot be empty.");
        if (string.IsNullOrWhiteSpace(result.Host)) throw new ArgumentException("--host cannot be empty.");
        if (result.HardRecoveryTimeoutSeconds <= result.RequestTimeoutSeconds)
            throw new ArgumentException("--hard-recovery-timeout-seconds must be greater than --request-timeout-seconds.");
        if (result.Host == "0.0.0.0" && string.IsNullOrWhiteSpace(result.AccessToken))
            throw new ArgumentException("LAN sharing requires ZEMAX_MCP_TOKEN to be configured.");
        return result;
    }
}

internal sealed class StdioMcpBridge : IDisposable
{
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxTrackedClients = 20;
    private const string DefaultMcpProtocolVersion = "2025-03-26";
    private static readonly HashSet<string> SupportedMcpProtocolVersions = new HashSet<string>(StringComparer.Ordinal)
    {
        DefaultMcpProtocolVersion
    };
    private static readonly TimeSpan ClientSessionIdleTimeout = TimeSpan.FromMinutes(15);
    // An initialize response is the only point at which a provisional session
    // can become visible. Keep abandoned handshakes short-lived so a client
    // disconnect cannot consume the single-client slot for fifteen minutes.
    private static readonly TimeSpan ProvisionalSessionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SseWriteTimeout = TimeSpan.FromSeconds(10);
    private readonly BridgeOptions _options;
    // A main MCP request may hold the OpticStudio session for minutes.  Never
    // use this lock for responses to server requests or client notifications:
    // those messages must be able to reach stdio while the main request waits.
    private readonly SemaphoreSlim _requestExecutionLock = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _stdinWriteLock = new SemaphoreSlim(1, 1);
    private readonly object _stateLock = new object();
    private readonly Dictionary<string, ClientActivity> _clients = new Dictionary<string, ClientActivity>(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveRequest> _activeOperations = new Dictionary<string, ActiveRequest>(StringComparer.Ordinal);
    private readonly Dictionary<string, JobActivity> _jobs = new Dictionary<string, JobActivity>(StringComparer.Ordinal);
    private readonly Dictionary<string, ResponseWaiter> _responseWaiters = new Dictionary<string, ResponseWaiter>(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingServerRequest> _pendingServerRequests = new Dictionary<string, PendingServerRequest>(StringComparer.Ordinal);
    private readonly Dictionary<string, SseResponseStream> _sseStreams = new Dictionary<string, SseResponseStream>(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Process? _server;
    private NamedPipeServerStream? _workerPipe;
    private StreamReader? _workerReader;
    private StreamWriter? _workerWriter;
    private HttpListener? _listener;
    private DateTimeOffset? _lastRequestAt;
    private string _lastClient = "None yet";
    private bool _zosApiLoaded;
    private bool _zosApiConnected;
    private string _licenseStatus = "Not checked";
    private string _zemaxDataDirectory = "Not reported";
    private string? _loadedZosApiPath;
    private string? _loadedInterfacesPath;
    private string? _loadedNetHelperPath;
    private string? _lastSnapshotPath;
    private DateTimeOffset? _serverStartedAt;
    private string? _lastServerError;
    private int _serverRestartCount;
    private int _consecutiveServerFailures;
    private int _restartLoopRunning;
    private int _activeRequests;
    private int _queuedRequests;
    private int _hardRecoveryCount;
    private int _remainingTestInitializeWriteFailures;
    private bool _disposed;

    public StdioMcpBridge(BridgeOptions options)
    {
        _options = options;
        _remainingTestInitializeWriteFailures = options.TestFailInitializeResponseWrite ? 1 : 0;
    }

    public async Task RunAsync()
    {
        StartServer(isRestart: false);
        _listener = new HttpListener();
        // HTTP.SYS uses '+' as the all-interface prefix.  The launcher creates
        // its URL ACL only when the user explicitly enables LAN sharing.
        var listenerHost = _options.Host == "0.0.0.0" ? "+" : _options.Host;
        _listener.Prefixes.Add($"http://{listenerHost}:{_options.Port}{_options.Path}");
        _listener.Start();
        Log.Information("Zemax MCP HTTP endpoint listening at {Url}", _listener.Prefixes.FirstOrDefault());

        while (_listener.IsListening)
        {
            var context = await _listener.GetContextAsync().ConfigureAwait(false);
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private void StartServer(bool isRestart)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StdioMcpBridge));
        DisposeServerProcess();
        var pipeName = "ZemaxMcpWorker-" + Process.GetCurrentProcess().Id + "-" + Guid.NewGuid().ToString("N");
        var pipeSecret = _options.UseStdioBackend ? null : CreatePipeSecret();
        NamedPipeServerStream? workerPipe = null;
        if (!_options.UseStdioBackend) workerPipe = CreatePrivateWorkerPipe(pipeName);
        var psi = new ProcessStartInfo(_options.ServerPath, _options.UseStdioBackend ? "" : "--pipe \"" + pipeName + "\"")
        {
            WorkingDirectory = System.IO.Path.GetDirectoryName(_options.ServerPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = _options.UseStdioBackend,
            RedirectStandardOutput = _options.UseStdioBackend,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Authentication terminates at the HTTP bridge. The ZOS-API subprocess
        // does not need the LAN secret and must not inherit it accidentally.
        psi.EnvironmentVariables.Remove("ZEMAX_MCP_TOKEN");
        if (!string.IsNullOrWhiteSpace(_options.ZemaxRoot)) psi.EnvironmentVariables["ZEMAX_ROOT"] = _options.ZemaxRoot;
        psi.EnvironmentVariables["ZEMAX_MCP_READ_ONLY"] = _options.ReadOnly ? "1" : "0";
        psi.EnvironmentVariables["ZEMAX_MCP_TOOLSET"] = _options.ToolsetProfile;
        psi.EnvironmentVariables["ZEMAX_MCP_SNAPSHOT_DIR"] = _options.SnapshotDirectory;
        if (!string.IsNullOrWhiteSpace(pipeSecret)) psi.EnvironmentVariables["ZEMAX_MCP_PIPE_SECRET"] = pipeSecret;
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to launch the Zemax MCP Worker."); }
        catch
        {
            workerPipe?.Dispose();
            throw;
        }
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) => HandleServerStatus(e.Data);
        process.Exited += (_, _) => HandleServerExited(process);
        _server = process;
        try
        {
            if (!_options.UseStdioBackend)
            {
                WaitForWorkerStartup(workerPipe!.WaitForConnectionAsync(), process, "connect to the private Worker pipe");
                _workerPipe = workerPipe;
                _workerReader = new StreamReader(workerPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                _workerWriter = new StreamWriter(workerPipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true) { AutoFlush = true };
                AuthenticateWorkerPipe(process, pipeSecret!);
            }
        }
        catch
        {
            try { _workerWriter?.Dispose(); } catch { }
            try { _workerReader?.Dispose(); } catch { }
            try { workerPipe?.Dispose(); } catch { }
            _workerWriter = null;
            _workerReader = null;
            _workerPipe = null;
            try { if (!process.HasExited) process.Kill(); } catch { }
            _server = null;
            process.Dispose();
            throw;
        }
        lock (_stateLock)
        {
            _zosApiLoaded = false;
            _zosApiConnected = false;
            _licenseStatus = "Not checked";
            _zemaxDataDirectory = "Not reported";
            _loadedZosApiPath = null;
            _loadedInterfacesPath = null;
            _loadedNetHelperPath = null;
            _activeOperations.Clear();
            _jobs.Clear();
            _serverStartedAt = DateTimeOffset.UtcNow;
            if (isRestart) _serverRestartCount++;
        }
        process.BeginErrorReadLine();
        _ = Task.Run(() => PumpServerOutputAsync(process));
        Log.Information(_options.UseStdioBackend
            ? "Started MCP test backend with PID {Pid}{Restart} over stdio"
            : "Started MCP Worker with PID {Pid}{Restart}; Host connected through private named pipe",
            process.Id, isRestart ? " after recovery" : string.Empty);
    }

    private static NamedPipeServerStream CreatePrivateWorkerPipe(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Could not determine the current Windows user for the private Worker pipe.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    private static string CreatePipeSecret()
    {
        var bytes = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private void WaitForWorkerStartup(Task task, Process process, string activity)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(_options.WorkerStartupTimeoutSeconds);
        while (!task.Wait(100))
        {
            if (process.HasExited)
                throw new InvalidOperationException("The MCP Worker exited before it could " + activity + ". Exit code: " + process.ExitCode + ".");
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out after " + _options.WorkerStartupTimeoutSeconds + " seconds waiting for the MCP Worker to " + activity + ".");
        }
        task.GetAwaiter().GetResult();
    }

    private void AuthenticateWorkerPipe(Process process, string secret)
    {
        var reader = _workerReader ?? throw new InvalidOperationException("The private Worker pipe reader was not created.");
        var writer = _workerWriter ?? throw new InvalidOperationException("The private Worker pipe writer was not created.");
        var expected = "ZEMAX_MCP_PIPE_HELLO|" + process.Id + "|" + secret;
        var helloTask = reader.ReadLineAsync();
        WaitForWorkerStartup(helloTask, process, "complete the private Worker pipe handshake");
        if (!string.Equals(helloTask.GetAwaiter().GetResult(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The private Worker pipe handshake did not identify the launched Worker process.");
        writer.WriteLine("ZEMAX_MCP_PIPE_OK");
        writer.Flush();
    }

    private void HandleServerStatus(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        var statusMessage = message!;
        if (statusMessage == "ZEMAX_MCP_STATUS:ZOS_API_LOADED")
        {
            lock (_stateLock) { _zosApiLoaded = true; _consecutiveServerFailures = 0; _lastServerError = null; }
            Log.Information("ZOS-API assemblies loaded from the local OpticStudio installation.");
            return;
        }
        if (statusMessage == "ZEMAX_MCP_STATUS:ZOS_API_CONNECTED")
        {
            lock (_stateLock) { _zosApiConnected = true; _consecutiveServerFailures = 0; _lastServerError = null; }
            Log.Information("Connected to OpticStudio through ZOS-API.");
            return;
        }
        const string licenseValid = "ZEMAX_MCP_STATUS:ZOS_LICENSE_VALID:";
        const string licenseInvalid = "ZEMAX_MCP_STATUS:ZOS_LICENSE_INVALID:";
        const string dataDirectory = "ZEMAX_MCP_STATUS:ZEMAX_DATA_DIR:";
        const string zosApiAssembly = "ZEMAX_MCP_STATUS:ZOSAPI_ASSEMBLY:";
        const string interfacesAssembly = "ZEMAX_MCP_STATUS:ZOSAPI_INTERFACES_ASSEMBLY:";
        const string netHelperAssembly = "ZEMAX_MCP_STATUS:ZOSAPI_NETHELPER_ASSEMBLY:";
        const string snapshotCreated = "ZEMAX_MCP_STATUS:SNAPSHOT_CREATED:";
        const string jobStatus = "ZEMAX_MCP_STATUS:JOB:";
        if (statusMessage.StartsWith(licenseValid, StringComparison.Ordinal))
        {
            lock (_stateLock) _licenseStatus = "Valid — " + statusMessage.Substring(licenseValid.Length);
            Log.Information("ZOS-API license validated.");
            return;
        }
        if (statusMessage.StartsWith(licenseInvalid, StringComparison.Ordinal))
        {
            lock (_stateLock) _licenseStatus = "Invalid — " + statusMessage.Substring(licenseInvalid.Length);
            Log.Warning("ZOS-API reported an invalid license.");
            return;
        }
        if (statusMessage.StartsWith(dataDirectory, StringComparison.Ordinal))
        {
            var reported = statusMessage.Substring(dataDirectory.Length);
            lock (_stateLock) _zemaxDataDirectory = string.IsNullOrWhiteSpace(reported) ? "Not reported" : reported;
            Log.Information("OpticStudio reported its runtime Data directory.");
            return;
        }
        if (statusMessage.StartsWith(zosApiAssembly, StringComparison.Ordinal))
        {
            lock (_stateLock) _loadedZosApiPath = statusMessage.Substring(zosApiAssembly.Length);
            return;
        }
        if (statusMessage.StartsWith(interfacesAssembly, StringComparison.Ordinal))
        {
            lock (_stateLock) _loadedInterfacesPath = statusMessage.Substring(interfacesAssembly.Length);
            return;
        }
        if (statusMessage.StartsWith(netHelperAssembly, StringComparison.Ordinal))
        {
            lock (_stateLock) _loadedNetHelperPath = statusMessage.Substring(netHelperAssembly.Length);
            return;
        }
        if (statusMessage.StartsWith(snapshotCreated, StringComparison.Ordinal))
        {
            lock (_stateLock) _lastSnapshotPath = statusMessage.Substring(snapshotCreated.Length);
            Log.Information("Created a pre-change lens snapshot.");
            return;
        }
        if (statusMessage.StartsWith(jobStatus, StringComparison.Ordinal))
        {
            var parts = statusMessage.Substring(jobStatus.Length).Split('|');
            if (parts.Length == 5)
            {
                double? progress = null;
                double parsedProgress = 0;
                if (!string.IsNullOrWhiteSpace(parts[3]) && !double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsedProgress)) return;
                if (!string.IsNullOrWhiteSpace(parts[3])) progress = parsedProgress;
                lock (_stateLock)
                {
                    if (!_jobs.TryGetValue(parts[0], out var job))
                    {
                        job = new JobActivity(parts[0], parts[1], DateTimeOffset.UtcNow);
                        _jobs[parts[0]] = job;
                    }
                    job.State = parts[2];
                    job.Progress = progress;
                    job.QueuePosition = int.TryParse(parts[4], out var position) ? position : 0;
                    if (job.State is "Completed" or "Cancelled" or "Failed") job.CompletedAt = DateTimeOffset.UtcNow;
                }
            }
            return;
        }
        Log.Warning("Server: {Message}", statusMessage);
    }

    private void HandleServerExited(Process process)
    {
        if (_disposed || !ReferenceEquals(_server, process)) return;
        string error;
        try { error = "MCP server exited with code " + process.ExitCode + "."; }
        catch { error = "MCP server exited unexpectedly."; }
        lock (_stateLock)
        {
            _zosApiLoaded = false;
            _zosApiConnected = false;
            _licenseStatus = "Not checked";
            _zemaxDataDirectory = "Not reported";
            _loadedZosApiPath = null;
            _loadedInterfacesPath = null;
            _loadedNetHelperPath = null;
            _activeOperations.Clear();
            _jobs.Clear();
            _lastServerError = error;
            _consecutiveServerFailures++;
            _clients.Clear(); // Force HTTP clients to initialize again after recovery.
        }
        Log.Error("{Error} Automatic recovery will be attempted.", error);
        FailResponseWaiters(process, new EndOfStreamException(error));
        ScheduleServerRestart();
    }

    private void ScheduleServerRestart()
    {
        if (_disposed || Interlocked.CompareExchange(ref _restartLoopRunning, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_disposed && !IsServerRunning())
                {
                    int failures;
                    lock (_stateLock) failures = _consecutiveServerFailures;
                    var delaySeconds = Math.Min(30, 1 << Math.Min(failures, 4));
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                    if (_disposed || IsServerRunning()) break;
                    await _requestExecutionLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!_disposed && !IsServerRunning()) StartServer(isRestart: true);
                    }
                    catch (Exception ex)
                    {
                        lock (_stateLock) { _lastServerError = ex.Message; _consecutiveServerFailures++; }
                        Log.Error(ex, "Could not restart the MCP stdio server");
                    }
                    finally { _requestExecutionLock.Release(); }
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.Exchange(ref _restartLoopRunning, 0);
                if (!_disposed && !IsServerRunning()) ScheduleServerRestart();
            }
        });
    }

    private bool IsServerRunning()
    {
        try { return _server != null && !_server.HasExited; }
        catch { return false; }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        SseResponseStream? sse = null;
        string? provisionalSession = null;
        string? activeOperationId = null;
        var initializationCommitted = false;
        var operationReleased = false;
        var operationOwnershipTransferredToRecovery = false;
        try
        {
            if (!ValidateOrigin(context)) return;
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                AddCorsHeaders(context);
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }
            if (!IsAuthorized(context.Request.Headers["Authorization"], _options.AccessToken))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                context.Response.Close();
                return;
            }
            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                context.Request.Url?.AbsolutePath.TrimEnd('/').EndsWith("/health", StringComparison.OrdinalIgnoreCase) == true)
            {
                await WriteJsonAsync(context, BuildHealthPayload()).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                var sessionId = context.Request.Headers["Mcp-Session-Id"];
                if (string.IsNullOrWhiteSpace(sessionId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.Close();
                    return;
                }
                if (SessionExists(sessionId) && !TryValidateMcpProtocolVersion(sessionId, context.Request.Headers["MCP-Protocol-Version"], out var deleteProtocolVersionError))
                {
                    await WriteJsonAsync(context, new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["error"] = new JObject { ["code"] = -32602, ["message"] = deleteProtocolVersionError },
                        ["id"] = null
                    }, HttpStatusCode.BadRequest).ConfigureAwait(false);
                    return;
                }
                if (SessionHasActiveOperation(sessionId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                    context.Response.Close();
                    return;
                }
                if (!RemoveSession(sessionId))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    return;
                }
                context.Response.StatusCode = (int)HttpStatusCode.OK;
                context.Response.Close();
                return;
            }
            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Close();
                return;
            }
            if (!AcceptsMcpResponse(context.Request.Headers["Accept"]))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotAcceptable;
                context.Response.Close();
                return;
            }
            if (context.Request.ContentLength64 > MaxRequestBytes)
            {
                await WriteJsonAsync(context, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JObject { ["code"] = -32600, ["message"] = "MCP request exceeds the 1 MiB bridge limit." },
                    ["id"] = null
                }, HttpStatusCode.RequestEntityTooLarge).ConfigureAwait(false);
                return;
            }

            var request = await ReadRequestAsync(context.Request).ConfigureAwait(false);
            var json = JObject.Parse(request);
            var now = DateTimeOffset.UtcNow;
            var method = json["method"]?.ToString() ?? "unknown";
            var id = json["id"];
            var isClientResponse = IsJsonRpcResponse(json);
            var isFastPathNotification = IsFastPathNotification(json);
            var isInitialize = string.Equals(method, "initialize", StringComparison.OrdinalIgnoreCase);
            if (!isClientResponse && (id == null || id.Type == JTokenType.Null) && !isFastPathNotification)
            {
                await WriteJsonAsync(context, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JObject { ["code"] = -32600, ["message"] = "Only notifications/* messages may omit a JSON-RPC id." },
                    ["id"] = null
                }, HttpStatusCode.BadRequest).ConfigureAwait(false);
                return;
            }
            var requestedSession = context.Request.Headers["Mcp-Session-Id"];
            string? responseSession = null;
            if (isInitialize)
            {
                if (!TryBeginClientRegistration(json, now, out responseSession, out var rejection))
                {
                    await WriteJsonAsync(context, new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["error"] = new JObject { ["code"] = -32002, ["message"] = rejection },
                        ["id"] = id
                    }, HttpStatusCode.Conflict).ConfigureAwait(false);
                    return;
                }
                requestedSession = responseSession;
                provisionalSession = responseSession;
            }
            else if (string.IsNullOrWhiteSpace(requestedSession))
            {
                await WriteJsonAsync(context, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JObject { ["code"] = -32000, ["message"] = "Mcp-Session-Id is required after initialize." },
                    ["id"] = id
                }, HttpStatusCode.BadRequest).ConfigureAwait(false);
                return;
            }
            else if (!SessionExists(requestedSession))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }
            else if (!isInitialize && !TryValidateMcpProtocolVersion(requestedSession, context.Request.Headers["MCP-Protocol-Version"], out var protocolVersionError))
            {
                await WriteJsonAsync(context, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JObject { ["code"] = -32602, ["message"] = protocolVersionError },
                    ["id"] = id
                }, HttpStatusCode.BadRequest).ConfigureAwait(false);
                return;
            }
            if (!isClientResponse && !isFastPathNotification && json["method"] == null)
            {
                await WriteJsonAsync(context, new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["error"] = new JObject { ["code"] = -32600, ["message"] = "JSON-RPC message must be a request, response, or notification." },
                    ["id"] = null
                }, HttpStatusCode.BadRequest).ConfigureAwait(false);
                return;
            }
            TouchClient(requestedSession, method, json, now);
            if (isClientResponse)
            {
                if (!TryTakePendingServerRequest(id!, requestedSession!, out var pendingServerRequest))
                {
                    await TryWriteRpcErrorAsync(context, -32004, "No matching pending server request exists for this JSON-RPC response.", HttpStatusCode.Conflict).ConfigureAwait(false);
                    return;
                }
                await WriteToServerAsync(pendingServerRequest.Server, request).ConfigureAwait(false);
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }
            if (isFastPathNotification)
            {
                await WriteToCurrentServerAsync(request).ConfigureAwait(false);
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }
            if (method.Equals("tools/call", StringComparison.OrdinalIgnoreCase) &&
                !ToolsetPolicy.IsToolAllowed(_options.ToolsetProfile, json["params"]?["name"]?.ToString()))
            {
                await TryWriteRpcErrorAsync(context, -32601,
                    "This tool is not enabled by the selected Launcher run configuration.",
                    HttpStatusCode.Forbidden).ConfigureAwait(false);
                return;
            }

            var operationId = BeginOperation(method, json, requestedSession, now);
            activeOperationId = operationId;
            // The session header must only be exposed after initialization has
            // completed successfully, so initialize always uses JSON.
            if (!isInitialize && id != null && ShouldUseSse(context.Request.Headers["Accept"]))
            {
                var candidateSse = new SseResponseStream(context, responseSession ?? requestedSession);
                if (_options.TestFailSseOpen) throw new IOException("Injected SSE-open failure for protocol regression verification.");
                await candidateSse.OpenAsync().ConfigureAwait(false);
                sse = candidateSse;
                RegisterSseStream(operationId, sse);
            }

            Interlocked.Increment(ref _activeRequests);
            var ownsRequestLock = false;
            string? response = null;
            var releaseRequestLock = true;
            try
            {
                await WaitForRequestExecutionLockAsync().ConfigureAwait(false);
                ownsRequestLock = true;
                if (!IsServerRunning()) StartServer(isRestart: true);
                var server = _server ?? throw new InvalidOperationException("The MCP server is not running.");
                var responseTask = !isClientResponse && id != null ? RegisterResponseWaiter(id, server, operationId, requestedSession!) : null;
                await WriteToServerAsync(server, request).ConfigureAwait(false);
                response = responseTask == null ? null : await ReadResponseWithTimeoutAsync(responseTask, server, operationId).ConfigureAwait(false);
            }
            catch (BridgeRequestTimeoutException ex) when (ex.ResponseIsStillDraining)
            {
                releaseRequestLock = false;
                operationOwnershipTransferredToRecovery = true;
                throw;
            }
            finally
            {
                if (releaseRequestLock)
                {
                    EndOperation(operationId);
                    operationReleased = true;
                    if (ownsRequestLock) _requestExecutionLock.Release();
                }
                Interlocked.Decrement(ref _activeRequests);
            }

            if (response == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }
            response = ToolsetPolicy.FilterToolsListResponse(_options.ToolsetProfile, method, response);
            var canCommitInitialization = provisionalSession != null && IsSuccessfulInitializeResponse(response);
            if (provisionalSession != null && !canCommitInitialization)
            {
                DiscardProvisionalClientRegistration(provisionalSession);
                responseSession = null;
            }
            if (sse != null)
                await WriteSseWithTimeoutAsync(sse, () => sse.WriteMessageAsync(response)).ConfigureAwait(false);
            else
            {
                if (canCommitInitialization && Interlocked.Decrement(ref _remainingTestInitializeWriteFailures) >= 0)
                    throw new IOException("Injected initialize-response write failure for protocol regression verification.");
                await WriteMcpJsonAsync(context, response, responseSession).ConfigureAwait(false);
            }

            // Commit only after the complete HTTP initialize response (including
            // its Mcp-Session-Id header) has been written without an exception.
            // If the client disconnects during that write, finally rolls the
            // provisional entry back instead of leaving an orphan client slot.
            if (canCommitInitialization)
            {
                var negotiatedProtocolVersion = GetNegotiatedMcpProtocolVersion(response);
                if (!CommitClientRegistration(provisionalSession!, negotiatedProtocolVersion))
                    throw new InvalidOperationException("The provisional MCP initialize session disappeared before it could be committed.");
                initializationCommitted = true;
            }
        }
        catch (RequestTooLargeException ex)
        {
            Log.Warning(ex, "Rejected an oversized MCP request");
            await WriteJsonAsync(context, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JObject { ["code"] = -32600, ["message"] = "MCP request exceeds the 1 MiB bridge limit." },
                ["id"] = null
            }, HttpStatusCode.RequestEntityTooLarge).ConfigureAwait(false);
        }
        catch (Newtonsoft.Json.JsonReaderException ex)
        {
            Log.Warning(ex, "Rejected malformed MCP JSON");
            await WriteJsonAsync(context, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JObject { ["code"] = -32700, ["message"] = "Malformed JSON-RPC request." },
                ["id"] = null
            }, HttpStatusCode.BadRequest).ConfigureAwait(false);
        }
        catch (BridgeRequestTimeoutException ex)
        {
            Log.Error(ex, "MCP request timed out; hard recovery is pending if the server remains unresponsive");
            if (sse != null) await WriteSseWithTimeoutAsync(sse, () => sse.WriteRpcErrorAsync(-32001, ex.Message)).ConfigureAwait(false);
            else await TryWriteRpcErrorAsync(context, -32001, ex.Message, HttpStatusCode.GatewayTimeout).ConfigureAwait(false);
        }
        catch (BridgeRequestQueueFullException ex)
        {
            Log.Warning(ex, "Rejected MCP request because the bridge queue is full");
            if (sse != null) await WriteSseWithTimeoutAsync(sse, () => sse.WriteRpcErrorAsync(-32003, ex.Message)).ConfigureAwait(false);
            else await TryWriteRpcErrorAsync(context, -32003, ex.Message, (HttpStatusCode)429).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP request failed");
            if (sse != null) await WriteSseWithTimeoutAsync(sse, () => sse.WriteRpcErrorAsync(-32603, "Zemax MCP bridge error")).ConfigureAwait(false);
            else await TryWriteRpcErrorAsync(context, -32603, "Zemax MCP bridge error", HttpStatusCode.InternalServerError).ConfigureAwait(false);
        }
        finally
        {
            // Opening an SSE response can fail before the execution-lock block
            // below is entered (for example when a client disconnects while the
            // response headers are being written).  The operation has already
            // been registered at that point, so clean it up here.  A soft timeout
            // deliberately transfers ownership to DrainOrRecoverAsync instead.
            if (!operationReleased && !operationOwnershipTransferredToRecovery && activeOperationId != null)
                EndOperation(activeOperationId);
            if (!initializationCommitted) DiscardProvisionalClientRegistration(provisionalSession);
            if (sse != null)
            {
                UnregisterSseStream(sse);
                await sse.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private bool ValidateOrigin(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin)) return true; // Native MCP clients normally omit Origin.
        if (IsOriginAllowed(origin, context.Request.Url))
        {
            AddCorsHeaders(context);
            return true;
        }
        Log.Warning("Rejected MCP request from untrusted Origin {Origin}", origin);
        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        context.Response.Close();
        return false;
    }

    private static void AddCorsHeaders(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        if (string.IsNullOrWhiteSpace(origin)) return;
        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Vary"] = "Origin";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type, Accept, Mcp-Session-Id, MCP-Protocol-Version";
    }

    internal static bool IsOriginAllowed(string origin, Uri? requestUrl)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var value)) return false;
        if (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps) return false;
        if (requestUrl == null) return false;
        if (value.Host.Equals(requestUrl.Host, StringComparison.OrdinalIgnoreCase)) return true;
        return value.IsLoopback && requestUrl.IsLoopback;
    }

    internal static bool IsAuthorized(string? authorization, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)) return true;
        const string prefix = "Bearer ";
        if (string.IsNullOrWhiteSpace(authorization)) return false;
        if (!authorization!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        return FixedTimeEquals(authorization!.Substring(prefix.Length).Trim(), expectedToken);
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(expected);
        var difference = left.Length ^ right.Length;
        var length = Math.Max(left.Length, right.Length);
        for (var i = 0; i < length; i++)
            difference |= (i < left.Length ? left[i] : 0) ^ (i < right.Length ? right[i] : 0);
        return difference == 0;
    }

    private JObject BuildHealthPayload()
    {
        lock (_stateLock)
        {
            var now = DateTimeOffset.UtcNow;
            var clients = new JArray(_clients.Values
                .OrderByDescending(x => x.LastRequestAt)
                .Select(x => new JObject
                {
                    ["name"] = x.Name,
                    ["version"] = x.Version,
                    ["connectedAt"] = x.ConnectedAt.ToString("O"),
                    ["lastRequestAt"] = x.LastRequestAt.ToString("O"),
                    ["lastMethod"] = x.LastMethod,
                    ["requestCount"] = x.RequestCount,
                    ["provisional"] = x.IsProvisional,
                    ["protocolVersion"] = x.ProtocolVersion,
                    ["active"] = now - x.LastRequestAt <= TimeSpan.FromMinutes(5)
                }));
            var activeOperations = new JArray(_activeOperations.Values
                .OrderBy(x => x.StartedAt)
                .Select(x => new JObject
                {
                    ["method"] = x.Method,
                    ["tool"] = x.ToolName,
                    ["startedAt"] = x.StartedAt.ToString("O"),
                    ["elapsedSeconds"] = Math.Max(0, (long)(now - x.StartedAt).TotalSeconds)
                }));
            var jobs = new JArray(_jobs.Values
                .OrderByDescending(x => x.StartedAt)
                .Select(x => new JObject
                {
                    ["jobId"] = x.JobId,
                    ["tool"] = x.ToolName,
                    ["state"] = x.State,
                    ["progress"] = x.Progress,
                    ["queuePosition"] = x.QueuePosition,
                    ["startedAt"] = x.StartedAt.ToString("O"),
                    ["elapsedSeconds"] = Math.Max(0, (long)((x.CompletedAt ?? now) - x.StartedAt).TotalSeconds)
                }));
            int? pid = null;
            try { if (IsServerRunning()) pid = _server!.Id; } catch { }
            var zemaxRoot = _options.ZemaxRoot ?? "";
            var netHelperPath = FindNetHelper(zemaxRoot);
            return new JObject
            {
                ["bridgeRunning"] = true,
                ["authenticationRequired"] = !string.IsNullOrWhiteSpace(_options.AccessToken),
                ["originValidationEnabled"] = true,
                ["readOnly"] = _options.ReadOnly,
                ["transport"] = _options.UseStdioBackend ? "stdio-test" : "private-named-pipe",
                ["toolsetProfile"] = _options.ToolsetProfile,
                ["enabledToolDomains"] = new JArray(ToolsetPolicy.EnabledDomains(_options.ToolsetProfile)),
                ["snapshotDirectory"] = _options.SnapshotDirectory,
                ["lastSnapshotPath"] = _lastSnapshotPath,
                ["bridgeStartedAt"] = _startedAt.ToString("O"),
                ["bridgeUptimeSeconds"] = Math.Max(0, (long)(now - _startedAt).TotalSeconds),
                ["mcpServerRunning"] = IsServerRunning(),
                ["mcpServerPid"] = pid,
                ["mcpServerStartedAt"] = _serverStartedAt?.ToString("O"),
                ["serverRestartCount"] = _serverRestartCount,
                ["hardRecoveryCount"] = _hardRecoveryCount,
                ["requestTimeoutSeconds"] = _options.RequestTimeoutSeconds,
                ["hardRecoveryTimeoutSeconds"] = _options.HardRecoveryTimeoutSeconds,
                ["workerStartupTimeoutSeconds"] = _options.WorkerStartupTimeoutSeconds,
                ["activeRequests"] = _activeRequests,
                ["queuedRequests"] = _queuedRequests,
                ["maxQueuedRequests"] = _options.MaxQueuedRequests,
                ["activeOperations"] = activeOperations,
                ["jobs"] = jobs,
                ["zosApiLoaded"] = _zosApiLoaded,
                ["zosApiConnected"] = _zosApiConnected,
                ["zemaxRoot"] = zemaxRoot,
                ["zosApiFiles"] = new JObject
                {
                    ["zosApi"] = ExistingPath(zemaxRoot, "ZOSAPI.dll"),
                    ["interfaces"] = ExistingPath(zemaxRoot, "ZOSAPI_Interfaces.dll"),
                    ["netHelper"] = netHelperPath
                },
                ["loadedZosApiFiles"] = new JObject
                {
                    ["zosApi"] = _loadedZosApiPath,
                    ["interfaces"] = _loadedInterfacesPath,
                    ["netHelper"] = _loadedNetHelperPath
                },
                ["licenseStatus"] = _licenseStatus,
                ["zemaxDataDirectory"] = _zemaxDataDirectory,
                ["lastServerError"] = _lastServerError,
                ["lastRequestAt"] = _lastRequestAt?.ToString("O"),
                ["lastClient"] = _lastClient,
                ["clientCount"] = clients.Count,
                ["provisionalSessionCount"] = _clients.Values.Count(x => x.IsProvisional),
                ["activeClientCount"] = _clients.Values.Count(x => now - x.LastRequestAt <= TimeSpan.FromMinutes(5)),
                ["maxActiveMcpClients"] = _options.MaxActiveMcpClients,
                ["clientIsolation"] = "single shared OpticStudio session; concurrent MCP clients are rejected",
                ["clients"] = clients
            };
        }
    }

    private bool TryBeginClientRegistration(JObject request, DateTimeOffset now, out string? sessionId, out string? rejection)
    {
        var name = request["params"]?["clientInfo"]?["name"]?.ToString();
        var version = request["params"]?["clientInfo"]?["version"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = "MCP client";
        sessionId = null;
        rejection = null;
        lock (_stateLock)
        {
            var expired = _clients
                .Where(pair => IsExpiredClientSession(pair.Key, pair.Value, now))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var key in expired) _clients.Remove(key);

            if (_clients.Count >= _options.MaxActiveMcpClients)
            {
                rejection = "This bridge intentionally allows only " + _options.MaxActiveMcpClients + " MCP client session because all requests share one OpticStudio/ZOS-API session. Disconnect the existing client or wait for its idle session to expire.";
                return false;
            }
            while (_clients.Count >= MaxTrackedClients)
            {
                var removable = _clients
                    .Where(pair => !SessionHasActiveOperation(pair.Key))
                    .OrderBy(pair => pair.Value.LastRequestAt)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (removable == null) break;
                _clients.Remove(removable);
            }
            if (_clients.Count >= MaxTrackedClients)
            {
                rejection = "The bridge session table is full while active operations are still running. Retry after the active operation finishes.";
                return false;
            }
            sessionId = Guid.NewGuid().ToString("N");
            _clients[sessionId] = new ClientActivity(name!, version ?? string.Empty, now, isProvisional: true);
        }
        return true;
    }

    private bool IsExpiredClientSession(string sessionId, ClientActivity client, DateTimeOffset now)
    {
        if (SessionHasActiveOperation(sessionId)) return false;
        var timeout = client.IsProvisional ? ProvisionalSessionTimeout : ClientSessionIdleTimeout;
        return now - client.LastRequestAt > timeout;
    }

    private bool SessionExists(string sessionId)
    {
        lock (_stateLock) return _clients.TryGetValue(sessionId, out var client) && !client.IsProvisional;
    }

    private bool CommitClientRegistration(string sessionId, string protocolVersion)
    {
        lock (_stateLock)
        {
            if (!_clients.TryGetValue(sessionId, out var client) || !client.IsProvisional || !SupportedMcpProtocolVersions.Contains(protocolVersion)) return false;
            client.IsProvisional = false;
            client.ProtocolVersion = protocolVersion;
            return true;
        }
    }

    private static string GetNegotiatedMcpProtocolVersion(string response)
    {
        try
        {
            var negotiated = JObject.Parse(response)["result"]?["protocolVersion"]?.ToString();
            return SupportedMcpProtocolVersions.Contains(negotiated ?? string.Empty) ? negotiated! : DefaultMcpProtocolVersion;
        }
        catch { return DefaultMcpProtocolVersion; }
    }

    private bool TryValidateMcpProtocolVersion(string sessionId, string? headerValue, out string error)
    {
        lock (_stateLock)
        {
            if (!_clients.TryGetValue(sessionId, out var client) || client.IsProvisional)
            {
                error = "Mcp-Session-Id is not an active initialized session.";
                return false;
            }
            var version = string.IsNullOrWhiteSpace(headerValue) ? client.ProtocolVersion : headerValue!.Trim();
            if (!SupportedMcpProtocolVersions.Contains(version))
            {
                error = "Unsupported MCP-Protocol-Version '" + version + "'. Supported versions: " + string.Join(", ", SupportedMcpProtocolVersions) + ".";
                return false;
            }
            if (!string.Equals(version, client.ProtocolVersion, StringComparison.Ordinal))
            {
                error = "MCP-Protocol-Version must match the version negotiated during initialize (" + client.ProtocolVersion + ").";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }

    private void DiscardProvisionalClientRegistration(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        lock (_stateLock)
        {
            if (_clients.TryGetValue(sessionId!, out var client) && client.IsProvisional)
                _clients.Remove(sessionId!);
        }
    }

    private void TouchClient(string? sessionId, string method, JObject request, DateTimeOffset now)
    {
        var detail = method;
        if (method.Equals("tools/call", StringComparison.OrdinalIgnoreCase))
        {
            var tool = request["params"]?["name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(tool)) detail += ": " + tool;
        }
        lock (_stateLock)
        {
            _lastRequestAt = now;
            if (!string.IsNullOrWhiteSpace(sessionId) && _clients.TryGetValue(sessionId!, out var activity))
            {
                activity.LastRequestAt = now;
                activity.LastMethod = detail;
                activity.RequestCount++;
                _lastClient = activity.Name;
            }
        }
    }

    private static string? ExistingPath(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var path = System.IO.Path.Combine(root, fileName);
        return File.Exists(path) ? path : null;
    }

    private string BeginOperation(string method, JObject request, string? sessionId, DateTimeOffset now)
    {
        var tool = method.Equals("tools/call", StringComparison.OrdinalIgnoreCase)
            ? request["params"]?["name"]?.ToString() ?? "tools/call"
            : method;
        var id = Guid.NewGuid().ToString("N");
        lock (_stateLock) _activeOperations[id] = new ActiveRequest(method, tool, sessionId, now);
        return id;
    }

    private void EndOperation(string operationId)
    {
        lock (_stateLock)
        {
            _activeOperations.Remove(operationId);
            foreach (var key in _pendingServerRequests.Where(pair => pair.Value.ParentOperationId == operationId).Select(pair => pair.Key).ToArray())
                _pendingServerRequests.Remove(key);
        }
    }

    private bool RemoveSession(string sessionId)
    {
        lock (_stateLock) return _clients.Remove(sessionId);
    }

    private bool SessionHasActiveOperation(string sessionId)
    {
        lock (_stateLock) return _activeOperations.Values.Any(operation =>
            string.Equals(operation.SessionId, sessionId, StringComparison.Ordinal));
    }

    private static bool AcceptsMcpResponse(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept)) return false;
        var accepted = accept!.Split(',').Select(x => x.Trim().Split(';')[0]);
        return accepted.Any(x => x.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                                 x.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) || x == "*/*");
    }

    private static bool ShouldUseSse(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept)) return false;
        return accept!.Split(',').Select(x => x.Trim().Split(';')[0])
            .Any(x => x.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindNetHelper(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        foreach (var relative in new[] { "ZOSAPI_NetHelper.dll", @"ZOS-API\Libraries\ZOSAPI_NetHelper.dll", @"ZOS_API\Libraries\ZOSAPI_NetHelper.dll" })
        {
            var path = System.IO.Path.Combine(root, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, JObject payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task WriteMcpJsonAsync(HttpListenerContext context, string response, string? sessionId)
    {
        var bytes = Encoding.UTF8.GetBytes(response);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        if (!string.IsNullOrWhiteSpace(sessionId)) context.Response.Headers["Mcp-Session-Id"] = sessionId;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task TryWriteRpcErrorAsync(HttpListenerContext context, int code, string message, HttpStatusCode status)
    {
        try
        {
            if (!context.Response.OutputStream.CanWrite) return;
            await WriteJsonAsync(context, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
                ["id"] = null
            }, status).ConfigureAwait(false);
        }
        catch (Exception writeError) { Log.Warning(writeError, "Could not write an HTTP MCP error response"); }
    }

    private static async Task<string> ReadRequestAsync(HttpListenerRequest request)
    {
        using (var body = new MemoryStream())
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0) break;
                if (body.Length + read > MaxRequestBytes)
                    throw new RequestTooLargeException();
                await body.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            }
            return request.ContentEncoding.GetString(body.ToArray());
        }
    }

    private async Task<string> ReadResponseWithTimeoutAsync(Task<string> responseTask, Process server, string operationId)
    {
        var completed = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds))).ConfigureAwait(false);
        if (completed == responseTask) return await responseTask.ConfigureAwait(false);

        var pending = new PendingResponse();
        _ = DrainOrRecoverAsync(responseTask, server, operationId, pending);
        var message = $"MCP request exceeded {_options.RequestTimeoutSeconds} seconds. The operation may still be running; the bridge will force a clean MCP server restart after {_options.HardRecoveryTimeoutSeconds} seconds if it does not finish.";
        lock (_stateLock) _lastServerError = message;
        throw new BridgeRequestTimeoutException(message, responseIsStillDraining: true);
    }

    private async Task DrainOrRecoverAsync(Task<string> responseTask, Process server, string operationId, PendingResponse pending)
    {
        try
        {
            var remaining = Math.Max(1, _options.HardRecoveryTimeoutSeconds - _options.RequestTimeoutSeconds);
            var completed = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(remaining))).ConfigureAwait(false);
            if (completed == responseTask)
            {
                try { await responseTask.ConfigureAwait(false); }
                catch (Exception ex) { Log.Warning(ex, "Timed-out MCP response ended with an error"); }
                return;
            }

            var message = $"MCP request did not finish within the {_options.HardRecoveryTimeoutSeconds}-second hard recovery limit. Terminating the stdio server to unblock future clients.";
            lock (_stateLock)
            {
                _hardRecoveryCount++;
                _lastServerError = message;
            }
            Log.Error(message);
            try
            {
                if (!server.HasExited) server.Kill();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not terminate the unresponsive MCP stdio server");
            }
        }
        finally
        {
            EndOperation(operationId);
            if (pending.TryComplete())
            {
                try { _requestExecutionLock.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private async Task WaitForRequestExecutionLockAsync()
    {
        if (_requestExecutionLock.Wait(0)) return;

        var queuePosition = Interlocked.Increment(ref _queuedRequests);
        if (queuePosition > _options.MaxQueuedRequests)
        {
            Interlocked.Decrement(ref _queuedRequests);
            throw new BridgeRequestQueueFullException("The MCP request queue is full. Wait for the active OpticStudio operation to finish before retrying.");
        }

        try { await _requestExecutionLock.WaitAsync().ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _queuedRequests); }
    }

    private async Task WriteToCurrentServerAsync(string message)
    {
        var server = _server;
        if (server == null || !IsServerRunning())
            throw new InvalidOperationException("The MCP stdio server is not running.");
        await WriteToServerAsync(server, message).ConfigureAwait(false);
    }

    private async Task WriteToServerAsync(Process server, string message)
    {
        await _stdinWriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(_server, server) || !IsServerRunning())
                throw new InvalidOperationException("The MCP Worker is no longer available.");
            if (_options.UseStdioBackend)
            {
                await server.StandardInput.WriteLineAsync(message).ConfigureAwait(false);
                await server.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            else
            {
                var writer = _workerWriter ?? throw new InvalidOperationException("The private Worker pipe is not connected.");
                await writer.WriteLineAsync(message).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        finally { _stdinWriteLock.Release(); }
    }

    private Task<string> RegisterResponseWaiter(JToken id, Process server, string operationId, string sessionId)
    {
        if (!ReferenceEquals(_server, server) || !IsServerRunning())
            throw new InvalidOperationException("The MCP stdio server is no longer available.");
        var key = ResponseKey(id);
        var waiter = new ResponseWaiter(operationId, sessionId);
        lock (_stateLock)
        {
            if (_responseWaiters.ContainsKey(key))
                throw new InvalidOperationException("A duplicate JSON-RPC request id is already waiting for a response.");
            _responseWaiters.Add(key, waiter);
        }
        return waiter.Completion.Task;
    }

    private async Task PumpServerOutputAsync(Process server)
    {
        var pumpFailed = false;
        try
        {
            while (!_disposed && ReferenceEquals(_server, server))
            {
                var line = _options.UseStdioBackend
                    ? await server.StandardOutput.ReadLineAsync().ConfigureAwait(false)
                    : await (_workerReader ?? throw new EndOfStreamException("The private Worker pipe is not connected.")).ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    pumpFailed = true;
                    break;
                }
                JObject message;
                try { message = JObject.Parse(line); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "MCP Worker wrote a non-JSON message to the private pipe");
                    continue;
                }

                ResponseWaiter? response = null;
                if (IsJsonRpcResponse(message))
                {
                    lock (_stateLock)
                    {
                        var key = ResponseKey(message["id"]!);
                        if (_responseWaiters.TryGetValue(key, out response)) _responseWaiters.Remove(key);
                    }
                }
                if (response != null) response.Completion.TrySetResult(line);
                else
                {
                    var owner = GetCurrentResponseWaiter();
                    if (IsJsonRpcServerRequest(message))
                    {
                        if (owner == null || !HasSseStream(owner.OperationId))
                        {
                            await RejectUndeliverableServerRequestAsync(server, message["id"]!).ConfigureAwait(false);
                            continue;
                        }
                        RegisterPendingServerRequest(message["id"]!, message["method"]!.ToString(), owner, server);
                        if (!await PublishServerMessageAsync(line, owner.OperationId).ConfigureAwait(false))
                        {
                            RemovePendingServerRequest(message["id"]!);
                            await RejectUndeliverableServerRequestAsync(server, message["id"]!).ConfigureAwait(false);
                        }
                        continue;
                    }
                    // Keep stdout processing ordered: a final response cannot
                    // close this SSE stream before its preceding notification.
                    await PublishServerMessageAsync(line, owner?.OperationId).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            pumpFailed = true;
            if (!_disposed) Log.Error(ex, "MCP Worker pipe pump failed");
        }
        finally
        {
            if (pumpFailed) TerminateServerAfterPumpFailure(server);
            FailResponseWaiters(server, new EndOfStreamException("MCP server closed stdout."));
        }
    }

    private void TerminateServerAfterPumpFailure(Process server)
    {
        if (_disposed || !ReferenceEquals(_server, server)) return;
        try
        {
            if (!server.HasExited) server.Kill();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not terminate the MCP Worker after its pipe pump failed");
        }
    }

    private ResponseWaiter? GetCurrentResponseWaiter()
    {
        lock (_stateLock)
        {
            // Stdio requests are serialized, so at most one response is awaiting the
            // shared backend.  Its operation owns any notifications or server requests.
            return _responseWaiters.Count == 1 ? _responseWaiters.Values.First() : null;
        }
    }

    private bool HasSseStream(string operationId)
    {
        lock (_stateLock) return _sseStreams.ContainsKey(operationId);
    }

    private async Task RejectUndeliverableServerRequestAsync(Process server, JToken id)
    {
        Log.Warning("Rejecting a server-initiated MCP request because its owning HTTP request cannot receive SSE.");
        var error = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JObject
            {
                ["code"] = -32601,
                ["message"] = "Client cannot receive server-initiated requests."
            }
        };
        try { await WriteToServerAsync(server, error.ToString(Newtonsoft.Json.Formatting.None)).ConfigureAwait(false); }
        catch (Exception ex) { Log.Warning(ex, "Could not reject an undeliverable server-initiated MCP request"); }
    }

    private void RegisterPendingServerRequest(JToken id, string method, ResponseWaiter owner, Process server)
    {
        var key = ResponseKey(id);
        lock (_stateLock)
        {
            if (_pendingServerRequests.ContainsKey(key))
                throw new InvalidOperationException("The MCP server emitted a duplicate pending request id.");
            _pendingServerRequests.Add(key, new PendingServerRequest(key, owner.SessionId, owner.OperationId, method, server));
        }
    }

    private bool TryTakePendingServerRequest(JToken id, string sessionId, out PendingServerRequest pending)
    {
        var key = ResponseKey(id);
        lock (_stateLock)
        {
            if (!_pendingServerRequests.TryGetValue(key, out pending!) ||
                !string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal) ||
                !_activeOperations.ContainsKey(pending.ParentOperationId))
            {
                pending = null!;
                return false;
            }
            _pendingServerRequests.Remove(key);
            return true;
        }
    }

    private void RemovePendingServerRequest(JToken id)
    {
        lock (_stateLock) _pendingServerRequests.Remove(ResponseKey(id));
    }

    private async Task<bool> PublishServerMessageAsync(string message, string? operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            Log.Warning("Dropped an unsolicited MCP server message because no active HTTP request owns it.");
            return false;
        }

        SseResponseStream? stream;
        lock (_stateLock) _sseStreams.TryGetValue(operationId!, out stream);
        if (stream == null)
        {
            Log.Warning("Dropped an MCP server message because request {OperationId} is not using an SSE response stream.", operationId);
            return false;
        }
        return await WriteSseWithTimeoutAsync(stream, () => stream.WriteMessageAsync(message)).ConfigureAwait(false);
    }

    private async Task<bool> WriteSseWithTimeoutAsync(SseResponseStream stream, Func<Task> write)
    {
        Task writeTask;
        try { writeTask = write(); }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not start an SSE write");
            UnregisterSseStream(stream);
            stream.Abort();
            return false;
        }

        var completed = await Task.WhenAny(writeTask, Task.Delay(SseWriteTimeout)).ConfigureAwait(false);
        if (completed != writeTask)
        {
            Log.Warning("SSE client did not accept a message within {TimeoutSeconds} seconds; closing its stream.", SseWriteTimeout.TotalSeconds);
            UnregisterSseStream(stream);
            stream.Abort();
            _ = ObserveTimedOutSseWriteAsync(writeTask);
            return false;
        }

        try
        {
            await writeTask.ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not forward an MCP message to its owning SSE stream");
            UnregisterSseStream(stream);
            stream.Abort();
            return false;
        }
    }

    private static async Task ObserveTimedOutSseWriteAsync(Task writeTask)
    {
        try { await writeTask.ConfigureAwait(false); }
        catch { }
    }

    private void RegisterSseStream(string operationId, SseResponseStream stream)
    {
        lock (_stateLock) _sseStreams[operationId] = stream;
    }

    private void UnregisterSseStream(SseResponseStream stream)
    {
        lock (_stateLock)
        {
            foreach (var key in _sseStreams.Where(pair => ReferenceEquals(pair.Value, stream)).Select(pair => pair.Key).ToArray())
                _sseStreams.Remove(key);
        }
    }

    private void FailResponseWaiters(Process server, Exception error)
    {
        ResponseWaiter[] waiters;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_server, server)) return;
            waiters = _responseWaiters.Values.ToArray();
            _responseWaiters.Clear();
            _pendingServerRequests.Clear();
        }
        foreach (var waiter in waiters) waiter.Completion.TrySetException(error);
    }

    private static bool IsJsonRpcResponse(JObject message)
    {
        var id = message["id"];
        return id != null && id.Type != JTokenType.Null && message["method"] == null &&
               (message["result"] != null || message["error"] != null);
    }

    private static bool IsFastPathNotification(JObject message)
    {
        var method = message["method"]?.ToString();
        var id = message["id"];
        return (id == null || id.Type == JTokenType.Null) && method != null &&
               method.StartsWith("notifications/", StringComparison.Ordinal);
    }

    private static bool IsJsonRpcServerRequest(JObject message) =>
        message["method"] != null && message["id"] != null && message["id"]!.Type != JTokenType.Null;

    private static bool IsSuccessfulInitializeResponse(string response)
    {
        try
        {
            var message = JObject.Parse(response);
            return message["result"] != null && message["error"] == null;
        }
        catch { return false; }
    }

    private static string ResponseKey(JToken id) => id.ToString(Newtonsoft.Json.Formatting.None);

    public void Dispose()
    {
        _disposed = true;
        try { _listener?.Close(); } catch { }
        DisposeServerProcess();
        _requestExecutionLock.Dispose();
        _stdinWriteLock.Dispose();
    }

    private void DisposeServerProcess()
    {
        var process = _server;
        _server = null;
        var writer = _workerWriter;
        var reader = _workerReader;
        var pipe = _workerPipe;
        _workerWriter = null;
        _workerReader = null;
        _workerPipe = null;
        try { writer?.Dispose(); } catch { }
        try { reader?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); } catch { }
        try { process.Dispose(); } catch { }
    }

    private sealed class ClientActivity
    {
        public ClientActivity(string name, string version, DateTimeOffset connectedAt, bool isProvisional = false)
        {
            Name = name;
            Version = version;
            ConnectedAt = connectedAt;
            LastRequestAt = connectedAt;
            LastMethod = "initialize";
            IsProvisional = isProvisional;
            ProtocolVersion = DefaultMcpProtocolVersion;
        }

        public string Name { get; }
        public string Version { get; }
        public DateTimeOffset ConnectedAt { get; }
        public DateTimeOffset LastRequestAt { get; set; }
        public string LastMethod { get; set; }
        public long RequestCount { get; set; }
        public bool IsProvisional { get; set; }
        public string ProtocolVersion { get; set; }
    }

    private sealed class ActiveRequest
    {
        public ActiveRequest(string method, string toolName, string? sessionId, DateTimeOffset startedAt)
        {
            Method = method;
            ToolName = toolName;
            SessionId = sessionId;
            StartedAt = startedAt;
        }
        public string Method { get; }
        public string ToolName { get; }
        public string? SessionId { get; }
        public DateTimeOffset StartedAt { get; }
    }

    private sealed class PendingResponse
    {
        private int _completed;
        public bool TryComplete() => Interlocked.Exchange(ref _completed, 1) == 0;
    }

    private sealed class ResponseWaiter
    {
        public ResponseWaiter(string operationId, string sessionId)
        {
            OperationId = operationId;
            SessionId = sessionId;
            Completion = new TaskCompletionSource<string>();
        }

        public string OperationId { get; }
        public string SessionId { get; }
        public TaskCompletionSource<string> Completion { get; }
    }

    private sealed class PendingServerRequest
    {
        public PendingServerRequest(string id, string sessionId, string parentOperationId, string method, Process server)
        {
            Id = id;
            SessionId = sessionId;
            ParentOperationId = parentOperationId;
            Method = method;
            Server = server;
        }

        public string Id { get; }
        public string SessionId { get; }
        public string ParentOperationId { get; }
        public string Method { get; }
        public Process Server { get; }
    }

    /// <summary>One Streamable HTTP response. It stays open until the final JSON-RPC response,
    /// and can receive any number of server notifications beforehand.</summary>
    private sealed class SseResponseStream
    {
        private readonly HttpListenerContext _context;
        private readonly string? _sessionId;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private int _closed;

        public SseResponseStream(HttpListenerContext context, string? sessionId)
        {
            _context = context;
            _sessionId = sessionId;
        }

        public async Task OpenAsync()
        {
            var response = _context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/event-stream; charset=utf-8";
            response.Headers["Cache-Control"] = "no-cache";
            response.SendChunked = true;
            if (!string.IsNullOrWhiteSpace(_sessionId)) response.Headers["Mcp-Session-Id"] = _sessionId;
            await WriteRawAsync(": stream-open\n\n").ConfigureAwait(false);
        }

        public Task WriteMessageAsync(string payload) => WriteEventAsync("message", payload);

        public Task WriteRpcErrorAsync(int code, string message) => WriteMessageAsync(new JObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
            ["id"] = null
        }.ToString(Newtonsoft.Json.Formatting.None));

        private async Task WriteEventAsync(string eventName, string payload)
        {
            var lines = payload.Replace("\r", string.Empty).Split(new[] { '\n' });
            var builder = new StringBuilder("event: ").Append(eventName).Append('\n');
            foreach (var line in lines) builder.Append("data: ").Append(line).Append('\n');
            builder.Append('\n');
            await WriteRawAsync(builder.ToString()).ConfigureAwait(false);
        }

        private async Task WriteRawAsync(string payload)
        {
            if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(SseResponseStream));
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _closed) != 0) throw new ObjectDisposedException(nameof(SseResponseStream));
                var bytes = Encoding.UTF8.GetBytes(payload);
                await _context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                await _context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _context.Response.Abort(); }
            catch { }
        }

        public async Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try { _context.Response.Close(); }
            catch { }
            finally { _writeLock.Release(); _writeLock.Dispose(); }
        }
    }

    private sealed class JobActivity
    {
        public JobActivity(string jobId, string toolName, DateTimeOffset startedAt)
        {
            JobId = jobId;
            ToolName = toolName;
            StartedAt = startedAt;
        }
        public string JobId { get; }
        public string ToolName { get; }
        public DateTimeOffset StartedAt { get; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string State { get; set; } = "Queued";
        public double? Progress { get; set; }
        public int QueuePosition { get; set; }
    }
}

internal sealed class RequestTooLargeException : Exception
{
    public RequestTooLargeException() : base("MCP request exceeds the 1 MiB bridge limit.") { }
}

/// <summary>
/// The Host is the single policy boundary for the client-visible tool surface.
/// Keep the categorization here rather than duplicating it in the Launcher:
/// the Launcher selects a profile, and the Host enforces it for tools/list and
/// tools/call alike.
/// </summary>
internal static class ToolsetPolicy
{
    internal const string FullExpert = ToolsetCatalog.FullExpert;
    internal static string NormalizeProfile(string? profile) => ToolsetCatalog.NormalizeProfile(profile);
    internal static IEnumerable<string> EnabledDomains(string profile) => ToolsetCatalog.EnabledDomains(profile);
    internal static bool IsToolAllowed(string profile, string? toolName) => ToolsetCatalog.IsToolAllowed(profile, toolName);

    internal static string FilterToolsListResponse(string profile, string method, string response)
    {
        if (!method.Equals("tools/list", StringComparison.OrdinalIgnoreCase) || profile == FullExpert) return response;
        try
        {
            var message = JObject.Parse(response);
            if (message["result"]?["tools"] is not JArray tools) return response;
            foreach (var tool in tools.OfType<JObject>().ToArray())
            {
                if (!IsToolAllowed(profile, tool["name"]?.ToString())) tool.Remove();
            }
            return message.ToString(Newtonsoft.Json.Formatting.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not apply the selected toolset profile to tools/list");
            return response;
        }
    }

}

internal sealed class BridgeRequestTimeoutException : Exception
{
    public BridgeRequestTimeoutException(string message, bool responseIsStillDraining = false) : base(message) => ResponseIsStillDraining = responseIsStillDraining;
    public bool ResponseIsStillDraining { get; }
}

internal sealed class BridgeRequestQueueFullException : Exception
{
    public BridgeRequestQueueFullException(string message) : base(message) { }
}
