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
                default: throw new ArgumentException("Unknown bridge option: " + args[i]);
            }
        }
        if (string.IsNullOrWhiteSpace(result.ServerPath)) throw new ArgumentException("--server cannot be empty.");
        if (string.IsNullOrWhiteSpace(result.Host)) throw new ArgumentException("--host cannot be empty.");
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
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Process? _server;
    private HttpListener? _listener;
    private DateTimeOffset? _lastRequestAt;
    private string _lastClient = "None yet";
    private bool _zosApiLoaded;
    private bool _zosApiConnected;
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
        if (!string.IsNullOrWhiteSpace(_options.ZemaxRoot)) psi.EnvironmentVariables["ZEMAX_ROOT"] = _options.ZemaxRoot;
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to launch ZemaxMCP.Server.exe");
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) => HandleServerStatus(e.Data);
        process.Exited += (_, _) => HandleServerExited(process);
        _server = process;
        lock (_stateLock)
        {
            _zosApiLoaded = false;
            _zosApiConnected = false;
            _serverStartedAt = DateTimeOffset.UtcNow;
            if (isRestart) _serverRestartCount++;
        }
        process.BeginErrorReadLine();
        Log.Information("Started MCP stdio server with PID {Pid}{Restart}", process.Id, isRestart ? " after recovery" : string.Empty);
    }

    private void HandleServerStatus(string? message)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (message == "ZEMAX_MCP_STATUS:ZOS_API_LOADED")
        {
            lock (_stateLock) { _zosApiLoaded = true; _consecutiveServerFailures = 0; _lastServerError = null; }
            Log.Information("ZOS-API assemblies loaded from the local OpticStudio installation.");
            return;
        }
        if (message == "ZEMAX_MCP_STATUS:ZOS_API_CONNECTED")
        {
            lock (_stateLock) { _zosApiConnected = true; _consecutiveServerFailures = 0; _lastServerError = null; }
            Log.Information("Connected to OpticStudio through ZOS-API.");
            return;
        }
        Log.Warning("Server: {Message}", message);
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
            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                context.Request.Url?.AbsolutePath.TrimEnd('/').EndsWith("/health", StringComparison.OrdinalIgnoreCase) == true)
            {
                await WriteJsonAsync(context, BuildHealthPayload()).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                context.Response.Close();
                return;
            }
            if (!context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
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

            Interlocked.Increment(ref _activeRequests);
            await _requestLock.WaitAsync().ConfigureAwait(false);
            string? response;
            try
            {
                if (!IsServerRunning()) StartServer(isRestart: true);
                var server = _server ?? throw new InvalidOperationException("The MCP server is not running.");
                await server.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                await server.StandardInput.FlushAsync().ConfigureAwait(false);
                response = id == null ? null : await ReadResponseWithTimeoutAsync(id, server).ConfigureAwait(false);
            }
            finally { _requestLock.Release(); Interlocked.Decrement(ref _activeRequests); }

            if (response == null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            if (!string.IsNullOrWhiteSpace(responseSession))
                context.Response.Headers["Mcp-Session-Id"] = responseSession;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            context.Response.Close();
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
            int? pid = null;
            try { if (IsServerRunning()) pid = _server!.Id; } catch { }
            return new JObject
            {
                ["bridgeRunning"] = true,
                ["bridgeStartedAt"] = _startedAt.ToString("O"),
                ["bridgeUptimeSeconds"] = Math.Max(0, (long)(now - _startedAt).TotalSeconds),
                ["mcpServerRunning"] = IsServerRunning(),
                ["mcpServerPid"] = pid,
                ["mcpServerStartedAt"] = _serverStartedAt?.ToString("O"),
                ["serverRestartCount"] = _serverRestartCount,
                ["activeRequests"] = _activeRequests,
                ["zosApiLoaded"] = _zosApiLoaded,
                ["zosApiConnected"] = _zosApiConnected,
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

    private static async Task WriteJsonAsync(HttpListenerContext context, JObject payload, HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
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

    private async Task<string> ReadResponseWithTimeoutAsync(JToken id, Process server)
    {
        var responseTask = ReadResponseAsync(id, server);
        var completed = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds))).ConfigureAwait(false);
        if (completed == responseTask) return await responseTask.ConfigureAwait(false);

        _ = responseTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        var message = $"MCP request timed out after {_options.RequestTimeoutSeconds} seconds. The server is restarting automatically.";
        lock (_stateLock) _lastServerError = message;
        DisposeServerProcess();
        lock (_stateLock) { _clients.Clear(); _zosApiLoaded = false; _zosApiConnected = false; }
        StartServer(isRestart: true);
        throw new BridgeRequestTimeoutException(message);
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
}

internal sealed class RequestTooLargeException : Exception
{
    public RequestTooLargeException() : base("MCP request exceeds the 1 MiB bridge limit.") { }
}

internal sealed class BridgeRequestTimeoutException : Exception
{
    public BridgeRequestTimeoutException(string message) : base(message) { }
}
