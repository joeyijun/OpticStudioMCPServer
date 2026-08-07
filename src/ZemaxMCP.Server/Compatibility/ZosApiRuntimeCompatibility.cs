using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ZemaxMCP.Server.Compatibility;

/// <summary>
/// Guards the deliberate release model where proprietary ZOS-API assemblies are
/// not redistributed. A Worker is compiled against one installed OpticStudio
/// API baseline, while the user's selected installation supplies the assemblies
/// at runtime. Running a newer-compiled Worker on an older interface assembly is
/// unsafe because added interface members can otherwise fail as MissingMethod or
/// TypeLoad errors after startup.
/// </summary>
internal static class ZosApiRuntimeCompatibility
{
    internal const string BuildInfoFileName = "ZOSAPI_BUILD_INFO.txt";
    private static readonly string[] Components = { "ZOSAPI_Interfaces", "ZOSAPI", "ZOSAPI_NetHelper" };

    internal sealed class Report
    {
        public Report(bool baselinePresent, IReadOnlyList<string> messages)
        {
            BaselinePresent = baselinePresent;
            Messages = messages;
        }

        public bool BaselinePresent { get; }
        public IReadOnlyList<string> Messages { get; }
    }

    internal static Report Validate(string baseDirectory, IReadOnlyDictionary<string, string> runtimeAssemblyPaths)
    {
        var markerPath = Path.Combine(baseDirectory, BuildInfoFileName);
        if (!File.Exists(markerPath))
        {
            return new Report(false, new[]
            {
                "No packaged ZOS-API build baseline marker is present. This is expected for developer builds; cross-version runtime compatibility was not preflighted."
            });
        }

        var buildInfo = ReadKeyValueFile(markerPath);
        if (!buildInfo.TryGetValue("format", out var format) || format != "1")
            throw new InvalidDataException($"Unsupported or malformed {BuildInfoFileName} format.");

        var messages = new List<string>();
        var incompatible = new List<string>();
        foreach (var component in Components)
        {
            if (!runtimeAssemblyPaths.TryGetValue(component, out var runtimePath) || string.IsNullOrWhiteSpace(runtimePath) || !File.Exists(runtimePath))
                throw new FileNotFoundException($"The selected OpticStudio installation did not load {component}.dll for compatibility validation.", runtimePath);

            var buildVersion = GetBuildComparableVersion(buildInfo, component);
            var runtimeVersion = GetFileComparableVersion(runtimePath);
            if (buildVersion == null)
            {
                messages.Add($"{component}: packaged build version could not be compared.");
                continue;
            }
            if (runtimeVersion == null)
            {
                messages.Add($"{component}: runtime version could not be compared with build baseline {buildVersion}.");
                continue;
            }

            messages.Add($"{component}: build baseline {buildVersion}; runtime {runtimeVersion}.");
            if (runtimeVersion.CompareTo(buildVersion) < 0)
                incompatible.Add($"{component} runtime {runtimeVersion} is older than build baseline {buildVersion}");
        }

        if (incompatible.Count > 0)
        {
            throw new NotSupportedException(
                "The selected OpticStudio ZOS-API is older than the API used to compile this Worker. " +
                "Running a newer-compiled Worker against older ZOS-API interfaces is not supported because API members may be missing. " +
                string.Join("; ", incompatible) + ". " +
                "Use a Zemax MCP release built against this OpticStudio version (or an older supported baseline), or select a newer OpticStudio installation.");
        }

        return new Report(true, messages);
    }

    internal static Version? ParseComparableVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"(?<!\d)(\d+)(?:\.(\d+))(?:\.(\d+))?(?:\.(\d+))?");
        if (!match.Success) return null;

        static int Part(Group group) => group.Success && int.TryParse(group.Value, out var parsed) ? parsed : 0;
        return new Version(Part(match.Groups[1]), Part(match.Groups[2]), Part(match.Groups[3]), Part(match.Groups[4]));
    }

    private static Version? GetBuildComparableVersion(IReadOnlyDictionary<string, string> buildInfo, string component)
    {
        foreach (var suffix in new[] { "fileVersion", "productVersion", "assemblyVersion" })
        {
            if (buildInfo.TryGetValue(component + "." + suffix, out var value))
            {
                var parsed = ParseComparableVersion(value);
                if (parsed != null) return parsed;
            }
        }
        return null;
    }

    private static Version? GetFileComparableVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var parsed = ParseComparableVersion(info.FileVersion) ?? ParseComparableVersion(info.ProductVersion);
            if (parsed != null) return parsed;
        }
        catch
        {
            // Fall through to CLR assembly identity.
        }

        try { return AssemblyName.GetAssemblyName(path).Version; }
        catch { return null; }
    }

    private static Dictionary<string, string> ReadKeyValueFile(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0)
                throw new InvalidDataException($"Malformed line in {BuildInfoFileName}: '{rawLine}'.");
            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();
            if (key.Length == 0 || !result.TryAdd(key, value))
                throw new InvalidDataException($"Duplicate or empty key in {BuildInfoFileName}: '{key}'.");
        }
        return result;
    }
}
