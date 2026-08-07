using System.Reflection;
using System.Runtime.CompilerServices;
using ZemaxMCP.Server.Compatibility;

internal static class ZosApiCompatibilityAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var numericYear = ZosApiRuntimeCompatibility.ParseComparableVersion("2026.1.2.3 build metadata");
        if (numericYear == null || numericYear != new Version(26, 1, 2, 3))
            throw new InvalidOperationException("Four-digit Ansys ZOS-API version normalization regressed.");
        var releaseName = ZosApiRuntimeCompatibility.ParseComparableVersion("Ansys Zemax OpticStudio 2024 R2.01");
        if (releaseName == null || releaseName != new Version(24, 2, 1, 0))
            throw new InvalidOperationException("Ansys R-release ZOS-API version normalization regressed.");
        var legacy = ZosApiRuntimeCompatibility.ParseComparableVersion("21.3.2");
        if (legacy == null || legacy != new Version(21, 3, 2, 0))
            throw new InvalidOperationException("Legacy Zemax ZOS-API version parsing regressed.");

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
