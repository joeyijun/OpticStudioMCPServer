using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

/// <summary>WPF-free diagnostics formatting and client-safe status
/// projection. No network access, private tokens or COM.</summary>
internal static class StatusPresentation
{
    internal static string FormatClientName(string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity)) return "AI client";
        if (identity!.StartsWith("token:", StringComparison.OrdinalIgnoreCase)) return "authenticated client";
        var name = identity.StartsWith("client:", StringComparison.OrdinalIgnoreCase) ? identity.Substring(7) : identity;
        var suffix = name.IndexOfAny(new[] { '@', '|' });
        if (suffix >= 0) name = name.Substring(0, suffix);
        return string.IsNullOrWhiteSpace(name) || name.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? "AI client" : name;
    }
    internal static string FormatTimeAgo(TimeSpan age) =>
        age.TotalSeconds < 10 ? "just now" :
        age.TotalMinutes < 1 ? (int)Math.Max(0, age.TotalSeconds) + "s ago" :
        age.TotalHours < 1 ? (int)age.TotalMinutes + "m ago" :
        age.TotalDays < 1 ? (int)age.TotalHours + "h ago" :
        (int)age.TotalDays + "d ago";
    internal static string FormatUptime(long? totalSeconds)
    {
        if (totalSeconds == null) return "unknown";
        var value = TimeSpan.FromSeconds(Math.Max(0, totalSeconds.Value));
        return value.TotalDays >= 1 ? ((int)value.TotalDays) + "d " + value.ToString(@"hh\:mm\:ss") : value.ToString(@"hh\:mm\:ss");
    }
    internal static string FormatZemaxPaths(ZemaxInstallation? installation, string? remoteRoot, JObject? remoteApi, JObject? loadedApi, string? runtimeData)
    {
        if (installation != null)
        {
            var lines = new List<string>
            {
                "OpticStudio folder: " + installation.Root + " (" + installation.DiscoverySource + ")",
                "ZOS-API: " + installation.ZosApiPath,
                "NetHelper: " + installation.NetHelperPath,
                "Detected Zemax data: " + (string.IsNullOrWhiteSpace(installation.DataDirectory) ? "not found" : installation.DataDirectory + " (" + installation.DataDirectorySource + ")")
            };
            AddLoadedApiPaths(lines, loadedApi);
            if (!string.IsNullOrWhiteSpace(runtimeData) && runtimeData != "Not reported") lines.Add("Runtime Zemax data: " + runtimeData);
            lines.Add("License setup: " + installation.LicenseEvidence);
            return string.Join("\n", lines);
        }
        if (!string.IsNullOrWhiteSpace(remoteRoot))
        {
            var lines = new List<string>
            {
                "Remote OpticStudio folder: " + remoteRoot,
                "Remote ZOS-API: " + (remoteApi?["zosApi"]?.ToString() ?? "not found"),
                "Remote NetHelper: " + (remoteApi?["netHelper"]?.ToString() ?? "not found")
            };
            AddLoadedApiPaths(lines, loadedApi);
            lines.Add("Remote Zemax data: " + (string.IsNullOrWhiteSpace(runtimeData) ? "not reported" : runtimeData));
            return string.Join("\n", lines);
        }
        return "OpticStudio and ZOS-API paths are reported by the Zemax computer after its bridge is updated.";
    }
    internal static void AddLoadedApiPaths(ICollection<string> lines, JObject? loadedApi)
    {
        if (loadedApi == null) return;
        foreach (var item in new[] { ("Loaded ZOS-API", "zosApi"), ("Loaded Interfaces", "interfaces"), ("Loaded NetHelper", "netHelper") })
        {
            var path = loadedApi[item.Item2]?.ToString();
            if (!string.IsNullOrWhiteSpace(path)) lines.Add(item.Item1 + ": " + path);
        }
    }
}
