using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ZemaxMCP.Core.Logging;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Documentation;
using ZemaxMCP.Rpc;
using ZemaxMCP.Server.Rpc;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.Server;

internal static class ServerApplication
{
    private static readonly JsonSerializerOptions PrivateRpcJson = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(string[] args)
    {
        var pipeName = ReadOption(args, "--pipe");
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new InvalidOperationException("The production Worker requires the private Host RPC pipe. Use the Host executable to start it.");

        NamedPipeClientStream? workerPipe = null;
        StreamReader? workerPipeReader = null;
        StreamWriter? workerPipeWriter = null;
        // The Worker owns no public transport. Suppress Console.Out so a native
        // dependency can never accidentally become a second protocol surface.
        Console.SetOut(TextWriter.Null);

        try
        {
            // Establish and verify the private contract before loading ZOS-API.
            // Mixed Host/Worker binaries fail immediately with a clear contract
            // mismatch instead of reaching reflection binding or COM execution.
            var pipeSecret = Environment.GetEnvironmentVariable("ZEMAX_MCP_PIPE_SECRET");
            if (string.IsNullOrWhiteSpace(pipeSecret))
                throw new InvalidOperationException("The private Worker pipe did not receive its handshake secret.");
            workerPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.Run(() => workerPipe.Connect()).ConfigureAwait(false);
            workerPipeReader = new StreamReader(workerPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
            workerPipeWriter = new StreamWriter(workerPipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true) { AutoFlush = true };
            var handshake = new WorkerHandshake
            {
                RpcVersion = ZemaxRpcProtocol.Version,
                WorkerProcessId = System.Diagnostics.Process.GetCurrentProcess().Id,
                Secret = pipeSecret,
                ManifestFingerprint = StaticToolManifest.ContractFingerprint
            };
            await workerPipeWriter.WriteLineAsync(JsonSerializer.Serialize(handshake, PrivateRpcJson)).ConfigureAwait(false);
            var acknowledgementLine = await workerPipeReader.ReadLineAsync().ConfigureAwait(false);
            var acknowledgement = string.IsNullOrWhiteSpace(acknowledgementLine)
                ? null
                : JsonSerializer.Deserialize<WorkerHandshakeAck>(acknowledgementLine, PrivateRpcJson);
            if (acknowledgement == null || !acknowledgement.Accepted ||
                acknowledgement.RpcVersion != ZemaxRpcProtocol.Version ||
                !string.Equals(acknowledgement.ManifestFingerprint, StaticToolManifest.ContractFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The private Worker pipe handshake was rejected by the Host: " +
                    (acknowledgement?.Error ?? "invalid acknowledgement"));
            }

            // Initialize ZOSAPI assembly resolver only after the Host/Worker
            // contract has been authenticated and version-matched.
            var zemaxRoot = Environment.GetEnvironmentVariable("ZEMAX_ROOT");
            if (!string.IsNullOrWhiteSpace(zemaxRoot))
            {
                AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
                {
                    var name = new AssemblyName(eventArgs.Name).Name;
                    if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("ZOSAPI", StringComparison.OrdinalIgnoreCase)) return null;
                    var candidate = Path.Combine(zemaxRoot, name + ".dll");
                    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
                };
            }
            var initialized = string.IsNullOrWhiteSpace(zemaxRoot)
                ? ZOSAPI_NetHelper.ZOSAPI_Initializer.Initialize()
                : ZOSAPI_NetHelper.ZOSAPI_Initializer.Initialize(zemaxRoot);
            if (!initialized)
                throw new InvalidOperationException($"Failed to initialize ZOS-API. ZEMAX_ROOT='{zemaxRoot ?? "<auto-detect>"}'.");

            var serilogPath = Path.Combine(AppContext.BaseDirectory, "logs", "zemaxmcp-.log");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(serilogPath, rollingInterval: RollingInterval.Day,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();
            Log.Information("Starting ZemaxMCP Worker RPC v{RpcVersion} manifest {ManifestFingerprint}",
                ZemaxRpcProtocol.Version, StaticToolManifest.ContractFingerprint);

            var builder = Host.CreateEmptyApplicationBuilder(settings: null);
            builder.Services.AddSerilog();
            builder.Services.Configure<ZemaxConnectionOptions>(options =>
            {
                options.Mode = ConnectionMode.Standalone;
                options.TimeoutSeconds = 30;
            });
            builder.Services.AddSingleton<IZemaxCommandLog>(_ =>
                new ZemaxCommandLog(Path.Combine(AppContext.BaseDirectory, "logs")));
            builder.Services.AddSingleton<IZemaxSession, ZemaxSession>();
            builder.Services.AddSingleton<OperandDatabase>();
            builder.Services.AddSingleton<OperandSearchService>();
            builder.Services.AddSingleton<ConstraintStore>();
            builder.Services.AddSingleton<MultistartState>();
            builder.Services.AddSingleton<McpJobManager>();
            builder.Services.AddSingleton<ZemaxMCP.Server.Tooling.WorkerToolRegistry>();

            var host = builder.Build();
            var commandLog = host.Services.GetRequiredService<IZemaxCommandLog>();
            Log.Information("ZEMAX Command Log: {LogPath}", commandLog.LogFilePath);

            var session = host.Services.GetRequiredService<IZemaxSession>();
            session.StartConnectInBackground(ConnectionMode.Standalone);
            Log.Information("OpticStudio background connection started");

            var rpcServer = new WorkerRpcServer(host.Services);
            await rpcServer.RunAsync(workerPipe, workerPipe, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Log.Fatal(ex, "Worker terminated unexpectedly");
            Environment.ExitCode = 1;
        }
        finally
        {
            workerPipeWriter?.Dispose();
            workerPipeReader?.Dispose();
            workerPipe?.Dispose();
            Log.CloseAndFlush();
        }
    }

    private static string? ReadOption(string[] args, string option)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        return null;
    }
}
