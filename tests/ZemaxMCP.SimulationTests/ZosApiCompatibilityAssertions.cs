using System.Reflection;
using System.Runtime.CompilerServices;
using ZemaxMCP.Server.Compatibility;

internal static class ZosApiCompatibilityAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var parsed = ZosApiRuntimeCompatibility.ParseComparableVersion("2026.1.2.3 build metadata");
        if (parsed == null || parsed != new Version(2026, 1, 2, 3))
            throw new InvalidOperationException("ZOS-API comparable version parsing regressed.");

        var root = Path.Combine(Path.GetTempPath(), "ZemaxMCP-zos-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runtimeAssembly = Assembly.GetExecutingAssembly().Location;
            var runtime = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ZOSAPI_Interfaces"] = runtimeAssembly,
                ["ZOSAPI"] = runtimeAssembly,
                ["ZOSAPI_NetHelper"] = runtimeAssembly
            };

            var noMarker = ZosApiRuntimeCompatibility.Validate(root, runtime);
            if (noMarker.BaselinePresent)
                throw new InvalidOperationException("Developer build without a marker was incorrectly reported as release-baselined.");

            WriteMarker(root, "0.0.0.0");
            var compatible = ZosApiRuntimeCompatibility.Validate(root, runtime);
            if (!compatible.BaselinePresent)
                throw new InvalidOperationException("Packaged compatibility marker was not detected.");

            WriteMarker(root, "9999.0.0.0");
            try
            {
                ZosApiRuntimeCompatibility.Validate(root, runtime);
                throw new InvalidOperationException("An older runtime was not rejected against a newer build baseline.");
            }
            catch (NotSupportedException)
            {
                // Expected.
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void WriteMarker(string root, string assemblyVersion)
    {
        File.WriteAllLines(Path.Combine(root, ZosApiRuntimeCompatibility.BuildInfoFileName), new[]
        {
            "format=1",
            "ZOSAPI_Interfaces.assemblyVersion=" + assemblyVersion,
            "ZOSAPI.assemblyVersion=" + assemblyVersion,
            "ZOSAPI_NetHelper.assemblyVersion=" + assemblyVersion
        });
    }
}
