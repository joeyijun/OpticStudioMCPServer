using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Reflection;
using System.IO.Pipes;
using System.Text;
using ZemaxMCP.Core.Logging;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Documentation;
using ZemaxMCP.Server.Rpc;
using ZemaxMCP.Server.Services.Jobs;

namespace ZemaxMCP.Server;

internal static class ServerApplication
{
    public static async Task RunAsync(string[] args)
    {
        var pipeName = ReadOption(args, "--pipe");
        NamedPipeClientStream? workerPipe = null;
        StreamReader? workerPipeReader = null;
        StreamWriter? workerPipeWriter = null;
        // Redirect Console.Out so ZOSAPI or another dependency cannot pollute
        // the optional developer stdio transport or the production named pipe.
        Console.SetOut(TextWriter.Null);

        // Initialize ZOSAPI assembly resolver BEFORE any ZOSAPI types are loaded.
        // The launcher sets ZEMAX_ROOT after detecting the selected OpticStudio version.
        // Keeping the implicit lookup as a fallback preserves the existing stdio workflow.
        var zemaxRoot = Environment.GetEnvironmentVariable("ZEMAX_ROOT");
        if (!string.IsNullOrWhiteSpace(zemaxRoot))
        {
            // ZOS-API is supplied by the user's licensed OpticStudio installation.
            // Never copy or redistribute those assemblies with this application.
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                var name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("ZOSAPI", StringComparison.OrdinalIgnoreCase)) return null;
                var candidate = Path.Combine(zemaxRoot, name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
        }
        var initialized = string.IsNullOrWhiteSpace(zemaxRoot)
            ? ZOSAPI_NetHelper.ZOSAPI_Initializer.Initialize()
            : ZOSAPI_NetHelper.ZOSAPI_Initializer.Initialize(zemaxRoot);

        if (!initialized)
        {
            throw new InvalidOperationException(
                $"Failed to initialize ZOS-API. ZEMAX_ROOT='{zemaxRoot ?? "<auto-detect>"}'.");
        }

        // Configure Serilog - write to file only (console interferes with stdio)
        var serilogPath = Path.Combine(AppContext.BaseDirectory, "logs", "zemaxmcp-.log");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(serilogPath, rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            Log.Information("Starting ZemaxMCP Worker");

            // Use CreateEmptyApplicationBuilder for stdio transport (avoids console output)
            var builder = Host.CreateEmptyApplicationBuilder(settings: null);

            // Add Serilog
            builder.Services.AddSerilog();

            // Add configuration
            builder.Services.Configure<ZemaxConnectionOptions>(options =>
            {
                options.Mode = ConnectionMode.Standalone;
                options.TimeoutSeconds = 30;
            });

            // Add command logging - creates a dedicated log file for all ZEMAX commands
            builder.Services.AddSingleton<IZemaxCommandLog>(sp =>
            {
                var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                return new ZemaxCommandLog(logDir);
            });

            // Add core services
            builder.Services.AddSingleton<IZemaxSession, ZemaxSession>();
            builder.Services.AddSingleton<OperandDatabase>();
            builder.Services.AddSingleton<OperandSearchService>();
            builder.Services.AddSingleton<ConstraintStore>();
            builder.Services.AddSingleton<MultistartState>();
            var jobManager = new McpJobManager();
            builder.Services.AddSingleton(jobManager);

            // The Worker keeps all ZOS-API state in this process. In normal
            // product operation the Host connects through a private named pipe;
            // stdio remains available only for developer diagnostics.
            if (!string.IsNullOrWhiteSpace(pipeName))
            {
                var pipeSecret = Environment.GetEnvironmentVariable("ZEMAX_MCP_PIPE_SECRET");
                if (string.IsNullOrWhiteSpace(pipeSecret))
                    throw new InvalidOperationException("The private Worker pipe did not receive its handshake secret.");
                workerPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await Task.Run(() => workerPipe.Connect()).ConfigureAwait(false);
                workerPipeReader = new StreamReader(workerPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                workerPipeWriter = new StreamWriter(workerPipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true) { AutoFlush = true };
                await workerPipeWriter.WriteLineAsync("ZEMAX_MCP_PIPE_HELLO|" + System.Diagnostics.Process.GetCurrentProcess().Id + "|" + pipeSecret).ConfigureAwait(false);
                var acknowledgement = await workerPipeReader.ReadLineAsync().ConfigureAwait(false);
                if (!string.Equals(acknowledgement, "ZEMAX_MCP_PIPE_OK", StringComparison.Ordinal))
                    throw new InvalidOperationException("The private Worker pipe handshake was rejected by the Host.");
            }

            // MCP terminates at the Host. The Worker registers only its own
            // protocol-neutral command registry and exposes it through RPC.
            builder.Services.AddSingleton<ZemaxMCP.Server.Tooling.WorkerToolRegistry>();
            var host = builder.Build();
            // Log the command log file location
            var commandLog = host.Services.GetRequiredService<IZemaxCommandLog>();
            Log.Information("ZEMAX Command Log: {LogPath}", commandLog.LogFilePath);

            // Start OpticStudio connection in background — don't block MCP handshake.
            // This ensures the MCP server responds to 'initialize' immediately,
            // avoiding startup timeouts in clients like Codex.
            var session = host.Services.GetRequiredService<IZemaxSession>();
            session.StartConnectInBackground(ConnectionMode.Standalone);
            Log.Information("OpticStudio background connection started");

            if (workerPipe == null)
                throw new InvalidOperationException("The production Worker requires the private Host RPC pipe. Use the Host executable to start it.");
            Log.Information("Worker configured for private versioned RPC transport.");
            var rpcServer = new WorkerRpcServer(host.Services);
            await rpcServer.RunAsync(workerPipe, workerPipe, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application terminated unexpectedly");
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
        {
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
        }
        return null;
    }
}
