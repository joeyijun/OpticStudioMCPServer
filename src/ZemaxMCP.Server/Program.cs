using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Serilog;
using System.Reflection;
using System.Globalization;
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

        if (initialized)
        {
            Console.Error.WriteLine("ZEMAX_MCP_STATUS:ZOS_API_LOADED");
            Console.Error.WriteLine("ZEMAX_MCP_STATUS:ZOSAPI_ASSEMBLY:" + typeof(ZOSAPI.ZOSAPI_Connection).Assembly.Location);
            Console.Error.WriteLine("ZEMAX_MCP_STATUS:ZOSAPI_INTERFACES_ASSEMBLY:" + typeof(ZOSAPI.IZOSAPI_Application).Assembly.Location);
            Console.Error.WriteLine("ZEMAX_MCP_STATUS:ZOSAPI_NETHELPER_ASSEMBLY:" + typeof(ZOSAPI_NetHelper.ZOSAPI_Initializer).Assembly.Location);
        }

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
            jobManager.JobChanged += job => Console.Error.WriteLine(string.Join("|", new[]
            {
                "ZEMAX_MCP_STATUS:JOB:" + job.JobId,
                job.ToolName,
                job.State.ToString(),
                job.Progress?.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
                job.QueuePosition.ToString(CultureInfo.InvariantCulture)
            }));
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
                Console.Error.WriteLine("ZEMAX_MCP_STATUS:WORKER_PIPE_CONNECTING");
                await Task.Run(() => workerPipe.Connect()).ConfigureAwait(false);
                workerPipeReader = new StreamReader(workerPipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                workerPipeWriter = new StreamWriter(workerPipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 4096, leaveOpen: true) { AutoFlush = true };
                await workerPipeWriter.WriteLineAsync("ZEMAX_MCP_PIPE_HELLO|" + System.Diagnostics.Process.GetCurrentProcess().Id + "|" + pipeSecret).ConfigureAwait(false);
                var acknowledgement = await workerPipeReader.ReadLineAsync().ConfigureAwait(false);
                if (!string.Equals(acknowledgement, "ZEMAX_MCP_PIPE_OK", StringComparison.Ordinal))
                    throw new InvalidOperationException("The private Worker pipe handshake was rejected by the Host.");
                Console.Error.WriteLine("ZEMAX_MCP_STATUS:WORKER_PIPE_CONNECTED");
            }

            // MCP is terminated by the .NET Host. The Worker retains the
            // existing attribute-driven binding catalogue during migration,
            // but exposes it only through the private typed RPC server below.
            var mcpServer = builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "zemax-mcp",
                    Version = typeof(ServerApplication).Assembly.GetName().Version?.ToString(3) ?? "unknown"
                };
            });
            mcpServer
            .WithTools<ZemaxMCP.Server.Tools.Catalog.ToolCatalogTool>()
            .WithTools<ZemaxMCP.Server.Tools.Jobs.McpJobTools>()
            // Analysis Tools
            .WithTools<ZemaxMCP.Server.Tools.Analysis.SpotDiagramTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.MtfAnalysisTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.GeometricMtfTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.RayTraceTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.RmsSpotTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.CardinalPointsTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.SeidelCoefficientsTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.LateralColorTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.LongitudinalAberrationTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.ChromaticFocalShiftTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.FieldCurvatureDistortionTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.RayFanTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.OpticalPathFanTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.PupilAberrationFanTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.FftMtfVsFieldTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.DiffractionEncircledEnergyTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.GeometricEncircledEnergyTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.GeometricMtfVsFieldTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.RelativeIlluminationTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.ExportAnalysisTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.GeometricImageAnalysisTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.PopTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.RayTraceExtendedTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.ApertureThroughputTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.FftPsfTool>()
            .WithTools<ZemaxMCP.Server.Tools.Analysis.HuygensPsfTool>()
            // Optimization Tools
            .WithTools<ZemaxMCP.Server.Tools.Optimization.GetMeritFunctionTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.AddOperandTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.RemoveOperandTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.OptimizeTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.OperandHelpTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.SearchOperandsTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.OptimizationWizardTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.HammerOptimizationTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.GlobalSearchTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.SaveMeritFunctionFileTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.LoadMeritFunctionFileTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.ForbesMeritFunctionTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.GetVariablesTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.SetVariableConstraintsTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.ConstrainedOptimizeTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.MultistartOptimizeTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.MultistartStatusTool>()
            .WithTools<ZemaxMCP.Server.Tools.Optimization.MultistartStopTool>()
            // Lens Data Tools
            .WithTools<ZemaxMCP.Server.Tools.LensData.GetSystemDataTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.GetSurfaceTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetSurfaceTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.AddSurfaceTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetFieldsTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetWavelengthsTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetNumberOfFieldsTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetNumberOfWavelengthsTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetApertureTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.GetSurfaceSolvesTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetSurfaceSolveTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.GetAsphericSurfaceTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetAsphericSurfaceTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetSurfaceParameterTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetSurfaceTypeTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.RemoveSurfaceTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.ListSurfaceTypesTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.GetExtraDataTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SetExtraDataTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.SurfaceApertureTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.OffAxisConicFreeformTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.GetGlobalMatrixTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.StopAndFirstOrderTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.VignettingTool>()
            .WithTools<ZemaxMCP.Server.Tools.LensData.FieldWavelengthStatusTool>()
            // Non-sequential and tolerancing tools
            .WithTools<ZemaxMCP.Server.Tools.NonSequential.GetNscObjectsTool>()
            .WithTools<ZemaxMCP.Server.Tools.NonSequential.GetNscDetectorTool>()
            .WithTools<ZemaxMCP.Server.Tools.NonSequential.GetNscObjectParametersTool>()
            .WithTools<ZemaxMCP.Server.Tools.Tolerancing.GetTolerancesTool>()
            // Configuration Tools
            .WithTools<ZemaxMCP.Server.Tools.Configuration.GetConfigurationTool>()
            .WithTools<ZemaxMCP.Server.Tools.Configuration.SetNumberOfConfigurationsTool>()
            .WithTools<ZemaxMCP.Server.Tools.Configuration.SetCurrentConfigurationTool>()
            .WithTools<ZemaxMCP.Server.Tools.Configuration.AddConfigurationOperandTool>()
            .WithTools<ZemaxMCP.Server.Tools.Configuration.DeleteConfigurationOperandTool>()
            .WithTools<ZemaxMCP.Server.Tools.Configuration.GetConfigurationOperandsTool>()
            .WithTools<ZemaxMCP.Server.Tools.Configuration.SetConfigurationOperandValueTool>()
            // System Tools
            .WithTools<ZemaxMCP.Server.Tools.System.OpenFileTool>()
            .WithTools<ZemaxMCP.Server.Tools.System.SaveFileTool>()
            .WithTools<ZemaxMCP.Server.Tools.System.NewSystemTool>()
            .WithTools<ZemaxMCP.Server.Tools.System.ConnectTool>()
            .WithTools<ZemaxMCP.Server.Tools.System.QuickFocusTool>()
            .WithTools<ZemaxMCP.Server.Tools.System.ScaleLensTool>()
            // System Settings Tools
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.GetRayAimingTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SetRayAimingTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.GetAfocalModeTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SetAfocalModeTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.GetApodizationTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SetApodizationTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.GetClearSemiDiameterMarginTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SetClearSemiDiameterMarginTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.GetMtfUnitsTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SetMtfUnitsTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SystemMetadataTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.EnvironmentTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.PolarizationTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.UnitsTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SystemFilesTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.ApertureSettingsTool>()
            .WithTools<ZemaxMCP.Server.Tools.SystemSettings.SystemExplorerStatusTool>()
            // Glass Catalog Tools
            .WithTools<ZemaxMCP.Server.Tools.GlassCatalog.GetGlassCatalogsTool>()
            .WithTools<ZemaxMCP.Server.Tools.GlassCatalog.GetGlassesTool>()
            .WithTools<ZemaxMCP.Server.Tools.GlassCatalog.FilterGlassesTool>()
            .WithTools<ZemaxMCP.Server.Tools.GlassCatalog.ExportGlassCatalogTool>()
            // Resources
            .WithResources<ZemaxMCP.Server.Resources.CurrentSystemResource>()
            .WithResources<ZemaxMCP.Server.Resources.MeritFunctionResource>()
            .WithResources<ZemaxMCP.Server.Resources.OperandDocumentationResource>()
            // Prompts
            .WithPrompts<ZemaxMCP.Server.Prompts.DesignPrompts>()
            .WithPrompts<ZemaxMCP.Server.Prompts.OptimizationPrompts>()
            .WithPrompts<ZemaxMCP.Server.Prompts.AnalysisPrompts>();

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
