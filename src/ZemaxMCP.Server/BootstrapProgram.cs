using System.Reflection;

namespace ZemaxMCP.Server;

/// <summary>
/// Minimal CLR entry point that deliberately contains no compile-time ZOS-API
/// type references. This must run before the server application because its
/// async state machine references ZOSAPI types and can otherwise cause the
/// CLR to bind ZOSAPI.dll before the program has registered AssemblyResolve.
/// </summary>
internal static class BootstrapProgram
{
    public static int Main(string[] args)
    {
        Console.SetOut(TextWriter.Null);
        RegisterZosApiResolver();

        try
        {
            var serverApplication = Assembly.GetExecutingAssembly()
                .GetType("ZemaxMCP.Server.ServerApplication", throwOnError: true)!;
            var main = serverApplication.GetMethod("RunAsync", BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException("The MCP server application entry point was not found.");
            var task = main.Invoke(null, new object[] { args }) as Task
                ?? throw new InvalidOperationException("The MCP server entry point did not return a Task.");
            task.GetAwaiter().GetResult();
            return Environment.ExitCode;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            Console.Error.WriteLine(ex.InnerException);
            return ex.InnerException.HResult;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return ex.HResult;
        }
    }

    private static void RegisterZosApiResolver()
    {
        var zemaxRoot = Environment.GetEnvironmentVariable("ZEMAX_ROOT")?.Trim().Trim('"');

        AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
        {
            var name = new AssemblyName(eventArgs.Name).Name;
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith("ZOSAPI", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var folder in CandidateFolders(name, zemaxRoot))
            {
                var candidate = Path.Combine(folder, name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        };

        // Load the interface assembly first. This makes binding deterministic
        // even when the CLR prepares the async server state machine eagerly.
        foreach (var assemblyName in new[] { "ZOSAPI_Interfaces", "ZOSAPI", "ZOSAPI_NetHelper" })
        {
            if (AppDomain.CurrentDomain.GetAssemblies().Any(x =>
                    string.Equals(x.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var folder in CandidateFolders(assemblyName, zemaxRoot))
            {
                var candidate = Path.Combine(folder, assemblyName + ".dll");
                if (!File.Exists(candidate)) continue;
                Assembly.LoadFrom(candidate);
                break;
            }
        }
    }

    private static IEnumerable<string> CandidateFolders(string assemblyName, string? zemaxRoot)
    {
        var isNetHelper = assemblyName.Equals("ZOSAPI_NetHelper", StringComparison.OrdinalIgnoreCase);
        if (isNetHelper) yield return AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(zemaxRoot))
        {
            yield return zemaxRoot!;
            yield return Path.Combine(zemaxRoot!, "ZOS-API", "Libraries");
            yield return Path.Combine(zemaxRoot!, "ZOS_API", "Libraries");
        }
        if (!isNetHelper) yield return AppContext.BaseDirectory;
    }
}
