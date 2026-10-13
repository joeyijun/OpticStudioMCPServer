using System;
using System.Linq;
using System.Reflection;

namespace ZemaxMCP.Build;

internal sealed class BuildStamp
{
    public string ProductVersion { get; set; } = "unknown";
    public string InformationalVersion { get; set; } = "unknown";
    public string? SourceCommit { get; set; }
}

internal static class BuildIdentity
{
    internal static BuildStamp Read(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var parts = info.Split('+');
        var commit = parts.Length > 1 ? parts[1].Split('.')[0] : "";
        return new BuildStamp { ProductVersion = parts[0], InformationalVersion = info,
            SourceCommit = commit.Length is >= 40 and <= 64 && commit.All(Uri.IsHexDigit) ? commit : null };
    }
}
