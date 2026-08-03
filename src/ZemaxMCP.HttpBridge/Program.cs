using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Serilog;

namespace ZemaxMCP.HttpBridge;

/// <summary>
/// A Windows-only, stateful HTTP-to-stdio MCP bridge.  It removes the Node.js /
/// supergateway dependency while keeping the established net48 ZOS-API server.
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
    public string ServerPath { get; private set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "ZemaxMCP.Server.exe");
    public string ZemaxRoot { get; private set; } = "";
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 8000;
    public string Path { get; private set; } = "/mcp/";
    public string LogDirectory { get; private set; } = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");
    public int RequestTimeoutSeconds { get; private set; } = 300;
    public string AccessToken { get; private set; } = Environment.GetEnvironmentVariable("ZEMAX_MCP_TOKEN") ?? "";
    public bool ReadOnly { get; private set; }
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
                case "--read-only":
                    if (!bool.TryParse(value, out var readOnly)) throw new ArgumentException("--read-only must be true or false.");
                    result.ReadOnly = readOnly;
                    break;
                case "--snapshot-dir": result.SnapshotDirectory = value; break;
                default: throw new ArgumentException("Unknown bridge option: " + args[i]);
            }
        }
        if (string.IsNullOrWhiteSpace(result.ServerPath)) throw new ArgumentException("--server cannot be empty.");
        if (string.IsNullOrWhiteSpace(result.Host)) throw new ArgumentException("--host cannot be empty.");
        if (result.Host == "0.0.0.0" && string.IsNullOrWhiteSpace(result.AccessToken))
            throw new ArgumentException("LAN sharing requires ZEMAX_MCP_TOKEN to be configured.");
        return result;
    }
}

internal sealed class StdioMcpBridge : IDisposable
{
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxTrackedClients = 20;
    private readonly BridgeOptions _options;
    private readonly SemaphoreSlim _requestLock = new SemaphoreSlim(1, 1);
    private readonly object _stateLock = new object();
    private readonly Dictionary<string, ClientActivity> _clients = new Dictionary<string, ClientActivity>(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveRequest> _activeOperations = new Dictionary<string, ActiveRequest>(StringComparer.Ordinal);
    private readonly Dictionary<string, JobActivity> _jobs = new Dictionary<string, JobActivity>(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Process? _server;
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
    private bool _disposed;

    public StdioMcpBridge(BridgeOptions options) => _options = options;

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
        var psi = new ProcessStartInfo(_options.ServerPath)
        {
            WorkingDirectory = System.IO.Path.GetDirectoryName(_options.ServerPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Authentication terminates at the HTTP bridge. The ZOS-API subprocess
        // does not need the LAN secret and must not inherit it accidentally.
        psi.EnvironmentVariables.Remove("ZEMAX_MCP_TOKEN");
        if (!string.IsNullOrWhiteSpace(_options.ZemaxRoot)) psi.EnvironmentVariables["ZEMAX_ROOT"] = _options.ZemaxRoot;
        psi.EnvironmentVariables["ZEMAX_MCP_READ_ONLY"] = _options.ReadOnly ? "1" : "0";
        psi.EnvironmentVariables["ZEMAX_MCP_SNAPSHOT_DIR"] = _options.SnapshotDirectory;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to launch ZemaxMCP.Server.exe");
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) => HandleServerStatus(e.Data);
        process.Exited += (_, _) => HandleServerExited(process);
        _server = process;
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
        Log.Information("Started MCP stdio server with PID {Pid}{Restart}", process.Id, isRestart ? " after recovery" : string.Empty);
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
                    await _requestLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        if (!_disposed && !IsServerRunning()) StartServer(isRestart: true);
                    }
                    catch (Exception ex)
                    {
                        lock (_stateLock) { _lastServerError = ex.Message; _consecutiveServerFailures++; }
                        Log.Error(ex, "Could not restart the MCP stdio server");
                    }
                    finally { _requestLock.Release(); }
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
            var requestedSession = context.Request.Headers["Mcp-Session-Id"];
            string? responseSession = null;
            if (string.Equals(method, "initialize", StringComparison.OrdinalIgnoreCase))
            {
                responseSession = RegisterClient(json, now);
                requestedSession = responseSession;
            }
            else if (!string.IsNullOrWhiteSpace(requestedSession) && !SessionExists(requestedSession))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }
            TouchClient(requestedSession, method, json, now);
            var operationId = BeginOperation(method, json, now);

            Interlocked.Increment(ref _activeRequests);
            await _requestLock.WaitAsync().ConfigureAwait(false);
            string? response;
            var releaseRequestLock = true;
            try
            {
                if (!IsServerRunning()) StartServer(isRestart: true);
                var server = _server ?? throw new InvalidOperationException("The MCP server is not running.");
                await server.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                await server.StandardInput.FlushAsync().ConfigureAwait(false);
                response = id == null ? null : await ReadResponseWithTimeoutAsync(id, server, operationId).ConfigureAwait(false);
            }
            catch (BridgeRequestTimeoutException ex) when (ex.ResponseIsStillDraining)
            {
                releaseRequestLock = false;
                throw;
            }
            finally
            {
                if (releaseRequestLock)
                {
                    EndOperation(operationId);
                    _requestLock.Release();
                }
                Interlocked.Decrement(ref _activeRequests);
            }

            if (response == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }
            if (PrefersSse(context.Request.Headers["Accept"]))
                await WriteSseAsync(context, response, responseSession).ConfigureAwait(false);
            else
                await WriteMcpJsonAsync(context, response, responseSession).ConfigureAwait(false);
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
            Log.Error(ex, "MCP request timed out; the stdio server was restarted");
            await TryWriteRpcErrorAsync(context, -32001, ex.Message, HttpStatusCode.GatewayTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "HTTP request failed");
            await TryWriteRpcErrorAsync(context, -32603, "Zemax MCP bridge error", HttpStatusCode.InternalServerError).ConfigureAwait(false);
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
                ["snapshotDirectory"] = _options.SnapshotDirectory,
                ["lastSnapshotPath"] = _lastSnapshotPath,
                ["bridgeStartedAt"] = _startedAt.ToString("O"),
                ["bridgeUptimeSeconds"] = Math.Max(0, (long)(now - _startedAt).TotalSeconds),
                ["mcpServerRunning"] = IsServerRunning(),
                ["mcpServerPid"] = pid,
                ["mcpServerStartedAt"] = _serverStartedAt?.ToString("O"),
                ["serverRestartCount"] = _serverRestartCount,
                ["activeRequests"] = _activeRequests,
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
                ["activeClientCount"] = _clients.Values.Count(x => now - x.LastRequestAt <= TimeSpan.FromMinutes(5) && !IsLauncherClient(x.Name)),
                ["clients"] = clients
            };
        }
    }

    private string RegisterClient(JObject request, DateTimeOffset now)
    {
        var name = request["params"]?["clientInfo"]?["name"]?.ToString();
        var version = request["params"]?["clientInfo"]?["version"]?.ToString();
        if (string.IsNullOrWhiteSpace(name)) name = "MCP client";
        var sessionId = Guid.NewGuid().ToString("N");
        lock (_stateLock)
        {
            while (_clients.Count >= MaxTrackedClients)
                _clients.Remove(_clients.OrderBy(x => x.Value.LastRequestAt).First().Key);
            _clients[sessionId] = new ClientActivity(name!, version ?? string.Empty, now);
        }
        return sessionId;
    }

    private bool SessionExists(string sessionId)
    {
        lock (_stateLock) return _clients.ContainsKey(sessionId);
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

    private static bool IsLauncherClient(string name) => name.Equals("zemax-mcp-launcher", StringComparison.OrdinalIgnoreCase);

    private static string? ExistingPath(string root, string fileName)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        var path = System.IO.Path.Combine(root, fileName);
        return File.Exists(path) ? path : null;
    }

    private string BeginOperation(string method, JObject request, DateTimeOffset now)
    {
        var tool = method.Equals("tools/call", StringComparison.OrdinalIgnoreCase)
            ? request["params"]?["name"]?.ToString() ?? "tools/call"
            : method;
        var id = Guid.NewGuid().ToString("N");
        lock (_stateLock) _activeOperations[id] = new ActiveRequest(method, tool, now);
        return id;
    }

    private void EndOperation(string operationId)
    {
        lock (_stateLock) _activeOperations.Remove(operationId);
    }

    private bool RemoveSession(string sessionId)
    {
        lock (_stateLock) return _clients.Remove(sessionId);
    }

    private static bool AcceptsMcpResponse(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept)) return false;
        var accepted = accept!.Split(',').Select(x => x.Trim().Split(';')[0]);
        return accepted.Any(x => x.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                                 x.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) || x == "*/*");
    }

    private static bool PrefersSse(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept)) return false;
        var values = accept!.Split(',').Select(x => x.Trim().Split(';')[0]).ToArray();
        return values.Any(x => x.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)) &&
               !values.Any(x => x.Equals("application/json", StringComparison.OrdinalIgnoreCase) || x == "*/*");
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

    private static async Task WriteSseAsync(HttpListenerContext context, string response, string? sessionId)
    {
        var payload = "event: message\n" + "data: " + response.Replace("\r", string.Empty).Replace("\n", "\\n") + "\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-cache";
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

    private async Task<string> ReadResponseWithTimeoutAsync(JToken id, Process server, string operationId)
    {
        var responseTask = ReadResponseAsync(id, server);
        var completed = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds))).ConfigureAwait(false);
        if (completed == responseTask) return await responseTask.ConfigureAwait(false);

        _ = responseTask.ContinueWith(t =>
        {
            var ignored = t.Exception;
            EndOperation(operationId);
            try { _requestLock.Release(); } catch (ObjectDisposedException) { }
        }, TaskScheduler.Default);
        var message = $"MCP request exceeded {_options.RequestTimeoutSeconds} seconds. The operation is still running and the bridge will keep the stdio session intact.";
        lock (_stateLock) _lastServerError = message;
        throw new BridgeRequestTimeoutException(message, responseIsStillDraining: true);
    }

    private static async Task<string> ReadResponseAsync(JToken id, Process server)
    {
        while (true)
        {
            var line = await server.StandardOutput.ReadLineAsync().ConfigureAwait(false);
            if (line == null) throw new EndOfStreamException("MCP server closed stdout.");
            var message = JObject.Parse(line);
            if (JToken.DeepEquals(message["id"], id)) return line;
            Log.Debug("Forwarded MCP notification: {Message}", line);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _listener?.Close(); } catch { }
        DisposeServerProcess();
        _requestLock.Dispose();
    }

    private void DisposeServerProcess()
    {
        var process = _server;
        _server = null;
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(); } catch { }
        try { process.Dispose(); } catch { }
    }

    private sealed class ClientActivity
    {
        public ClientActivity(string name, string version, DateTimeOffset connectedAt)
        {
            Name = name;
            Version = version;
            ConnectedAt = connectedAt;
            LastRequestAt = connectedAt;
            LastMethod = "initialize";
        }

        public string Name { get; }
        public string Version { get; }
        public DateTimeOffset ConnectedAt { get; }
        public DateTimeOffset LastRequestAt { get; set; }
        public string LastMethod { get; set; }
        public long RequestCount { get; set; }
    }

    private sealed class ActiveRequest
    {
        public ActiveRequest(string method, string toolName, DateTimeOffset startedAt)
        {
            Method = method;
            ToolName = toolName;
            StartedAt = startedAt;
        }
        public string Method { get; }
        public string ToolName { get; }
        public DateTimeOffset StartedAt { get; }
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

internal sealed class BridgeRequestTimeoutException : Exception
{
    public BridgeRequestTimeoutException(string message, bool responseIsStillDraining = false) : base(message) => ResponseIsStillDraining = responseIsStillDraining;
    public bool ResponseIsStillDraining { get; }
}
