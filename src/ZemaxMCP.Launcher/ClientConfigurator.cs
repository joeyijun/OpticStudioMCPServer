using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

// Client-specific configuration file adapters. Pure UI-independent service:
// preserve the existing public static Configurator entrypoints and the
// detection/configuration status contract consumed by MainWindow.
internal static class Configurator
{
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string EnvironmentPathOrDefault(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(configured) ? fallback : Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
    }
    private static string CodexHome => EnvironmentPathOrDefault("CODEX_HOME", Path.Combine(UserProfile, ".codex"));
    private static string CodexPath => Path.Combine(CodexHome, "config.toml");
    private static string ClaudeDesktopPath => Path.Combine(AppData, "Claude", "claude_desktop_config.json");
    private static string CursorPath => Path.Combine(UserProfile, ".cursor", "mcp.json");
    // Antigravity's current global configuration location is documented as
    // ~/.gemini/config/mcp_config.json. Keep the former location in the
    // candidate list so an established installation is updated in place.
    private static readonly string[] AntigravityConfigPaths =
    {
        Path.Combine(UserProfile, ".gemini", "config", "mcp_config.json"),
        Path.Combine(UserProfile, ".gemini", "antigravity", "mcp_config.json")
    };
    private static string AntigravityPath => AntigravityConfigPaths.FirstOrDefault(File.Exists) ?? AntigravityConfigPaths[0];
    private static string KimiHome => EnvironmentPathOrDefault("KIMI_CODE_HOME", Path.Combine(UserProfile, ".kimi-code"));
    private static string KimiPath => Path.Combine(KimiHome, "mcp.json");
    private static string WorkBuddyPath => Path.Combine(UserProfile, ".workbuddy", "mcp.json");
    private static string VsCodeDefaultPath => Path.Combine(AppData, "Code", "User", "mcp.json");
    public static readonly string[] KnownAliases = { "codex", "claude", "cursor", "antigravity", "gemini", "kimi", "workbuddy", "codebuddy", "vscode", "visual studio", "copilot" };

    public static void ConfigureClaudeDesktop(string url) => ConfigureClaudeDesktop(url, "");
    public static void ConfigureClaudeDesktop(string url, string token)
    {
        ValidateUrl(url);
        var proxy = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ZemaxMCP.ClientProxy.exe");
        if (!File.Exists(proxy)) throw new FileNotFoundException("The release package is missing ZemaxMCP.ClientProxy.exe, which Claude Desktop needs for a private HTTP/LAN endpoint.", proxy);
        ConfigureStdioProxyJson(ClaudeDesktopPath, proxy, url, token);
    }
    public static void ConfigureCursor(string url) => ConfigureCursor(url, "");
    public static void ConfigureCursor(string url, string token) => ConfigureJson(CursorPath, "mcpServers", url, token);
    public static void ConfigureAntigravity(string url) => ConfigureAntigravity(url, "");
    public static void ConfigureAntigravity(string url, string token)
    {
        ValidateUrl(url);
        ConfigureAntigravityJson(AntigravityPath, url, token);
    }
    public static void ConfigureKimi(string url) => ConfigureKimi(url, "");
    public static void ConfigureKimi(string url, string token) => ConfigureJson(KimiPath, "mcpServers", url, token, false, true);
    public static void ConfigureWorkBuddy(string url) => ConfigureWorkBuddy(url, "");
    public static void ConfigureWorkBuddy(string url, string token) => ConfigureJson(WorkBuddyPath, "mcpServers", url, token, false, false);

    public static List<ClientConfigurationStatus> GetClientStatuses(string expectedUrl) => GetClientStatuses(expectedUrl, "");
    public static List<ClientConfigurationStatus> GetClientStatuses(string expectedUrl, string expectedToken)
    {
        var vsCodePaths = GetVsCodeConfigPaths().ToArray();
        return new List<ClientConfigurationStatus>
        {
            new ClientConfigurationStatus("Codex", new[] { "codex" }, Directory.Exists(CodexHome), IsCodexConfigured(expectedUrl, expectedToken), CodexPath, ConfigureCodex),
            new ClientConfigurationStatus("Claude Desktop", new[] { "claude" }, Directory.Exists(Path.Combine(AppData, "Claude")), IsClaudeConfigured(expectedUrl, expectedToken), ClaudeDesktopPath, ConfigureClaudeDesktop),
            new ClientConfigurationStatus("Cursor", new[] { "cursor" }, Directory.Exists(Path.Combine(UserProfile, ".cursor")) || Directory.Exists(Path.Combine(AppData, "Cursor")) || Directory.Exists(Path.Combine(LocalAppData, "Cursor")), IsJsonConfigured(CursorPath, "mcpServers", expectedUrl, expectedToken), CursorPath, ConfigureCursor),
            new ClientConfigurationStatus("Google Antigravity", new[] { "antigravity", "gemini" }, Directory.Exists(Path.Combine(UserProfile, ".gemini")), IsAntigravityConfigured(expectedUrl, expectedToken), AntigravityPath, ConfigureAntigravity),
            new ClientConfigurationStatus("Kimi Code", new[] { "kimi" }, Directory.Exists(KimiHome), IsJsonConfigured(KimiPath, "mcpServers", expectedUrl, expectedToken), KimiPath, ConfigureKimi),
            new ClientConfigurationStatus("WorkBuddy", new[] { "workbuddy", "codebuddy" }, Directory.Exists(Path.Combine(UserProfile, ".workbuddy")) || Directory.Exists(Path.Combine(AppData, "WorkBuddy")) || Directory.Exists(Path.Combine(LocalAppData, "WorkBuddy")), IsJsonConfigured(WorkBuddyPath, "mcpServers", expectedUrl, expectedToken), WorkBuddyPath, ConfigureWorkBuddy),
            new ClientConfigurationStatus("VS Code / Copilot", new[] { "vscode", "visual studio", "copilot" }, Directory.Exists(Path.Combine(AppData, "Code")) || Directory.Exists(Path.Combine(LocalAppData, "Programs", "Microsoft VS Code")), vsCodePaths.Any(x => IsJsonConfigured(x, "servers", expectedUrl, expectedToken)), string.Join("; ", vsCodePaths), null)
        };
    }

    public static string GenericHttpJson(string url, string token) => new JObject
    {
        ["mcpServers"] = new JObject { ["zemax-mcp"] = CreateHttpEntry(url, token, true) }
    }.ToString();

    public static void ConfigureVsCode(string url, string token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The MCP endpoint must be an absolute HTTP or HTTPS address.", nameof(url));

        // VS Code owns user-profile and workspace configuration locations. Its documented
        // installation URI opens the native review/trust flow and prevents this launcher
        // from overwriting an unknown profile's mcp.json file.
        var server = new JObject
        {
            ["name"] = "zemax-mcp",
            ["type"] = "http",
            ["url"] = endpoint.AbsoluteUri
        };
        AddHeaders(server, token);
        var installUri = "vscode:mcp/install?" + Uri.EscapeDataString(server.ToString(Newtonsoft.Json.Formatting.None));
        Process.Start(new ProcessStartInfo(installUri) { UseShellExecute = true });
    }

    public static void ConfigureJson(string path, string property, string url, string token, bool includeType = true, bool includeKimiTimeouts = false)
    {
        ValidateUrl(url);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var servers = root[property] as JObject;
        if (servers == null)
        {
            servers = new JObject();
            root[property] = servers;
        }
        var entry = CreateHttpEntry(url, token, includeType);
        if (includeKimiTimeouts)
        {
            entry["startupTimeoutMs"] = 60000;
            entry["toolTimeoutMs"] = 300000;
        }
        servers["zemax-mcp"] = entry;
        WriteAtomically(path, root.ToString());
    }

    private static void ConfigureAntigravityJson(string path, string url, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var servers = root["mcpServers"] as JObject;
        if (servers == null)
        {
            servers = new JObject();
            root["mcpServers"] = servers;
        }

        // Google Antigravity uses serverUrl for every remote MCP transport.
        // Do not write url/httpUrl: those legacy fields are explicitly rejected.
        var entry = new JObject { ["serverUrl"] = url };
        AddHeaders(entry, token);
        servers["zemax-mcp"] = entry;
        WriteAtomically(path, root.ToString());
    }

    private static void ConfigureStdioProxyJson(string path, string proxyPath, string url, string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : new JObject();
        var servers = root["mcpServers"] as JObject;
        if (servers == null)
        {
            servers = new JObject();
            root["mcpServers"] = servers;
        }
        servers["zemax-mcp"] = new JObject
        {
            ["command"] = proxyPath,
            ["args"] = new JArray("--url", url),
            ["env"] = string.IsNullOrWhiteSpace(token) ? new JObject() : new JObject { ["ZEMAX_MCP_TOKEN"] = token }
        };
        WriteAtomically(path, root.ToString());
    }

    public static void ConfigureCodex(string url) => ConfigureCodex(url, "");
    public static void ConfigureCodex(string url, string token)
    {
        ValidateUrl(url);
        var path = CodexPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = File.Exists(path) ? File.ReadAllText(path) : "";
        var block = "[mcp_servers.zemax]\r\nurl = \"" + url + "\"\r\n" +
                    (string.IsNullOrWhiteSpace(token) ? "" : "http_headers = { Authorization = \"Bearer " + EscapeToml(token) + "\" }\r\n");
        content = Regex.Replace(content, @"(?ms)^\[mcp_servers\.zemax\].*?(?=^\[|\z)", block);
        if (!content.Contains("[mcp_servers.zemax]")) content += (content.EndsWith("\n") || content.Length == 0 ? "" : "\r\n") + block;
        WriteAtomically(path, content);
    }

    private static JObject CreateHttpEntry(string url, string token, bool includeType)
    {
        var entry = new JObject { ["url"] = url };
        if (includeType) entry.AddFirst(new JProperty("type", "http"));
        AddHeaders(entry, token);
        return entry;
    }
    private static void AddHeaders(JObject entry, string token)
    {
        if (!string.IsNullOrWhiteSpace(token)) entry["headers"] = new JObject { ["Authorization"] = "Bearer " + token };
    }
    private static bool HasExpectedToken(JToken? entry, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)) return true;
        return string.Equals(entry?["headers"]?["Authorization"]?.ToString(), "Bearer " + expectedToken, StringComparison.Ordinal);
    }
    private static bool IsJsonConfigured(string path, string property, string expectedUrl, string expectedToken)
    {
        try
        {
            var entry = File.Exists(path) ? JObject.Parse(File.ReadAllText(path))[property]?["zemax-mcp"] : null;
            return entry != null && UrlsEqual(entry["url"]?.ToString(), expectedUrl) && HasExpectedToken(entry, expectedToken);
        }
        catch { return false; }
    }
    private static bool IsAntigravityConfigured(string expectedUrl, string expectedToken)
    {
        foreach (var path in AntigravityConfigPaths)
        {
            try
            {
                var entry = File.Exists(path) ? JObject.Parse(File.ReadAllText(path))["mcpServers"]?["zemax-mcp"] : null;
                if (entry != null && UrlsEqual(entry["serverUrl"]?.ToString(), expectedUrl) && HasExpectedToken(entry, expectedToken))
                    return true;
            }
            catch { }
        }
        return false;
    }
    private static bool IsClaudeConfigured(string expectedUrl, string expectedToken)
    {
        try
        {
            var entry = File.Exists(ClaudeDesktopPath) ? JObject.Parse(File.ReadAllText(ClaudeDesktopPath))["mcpServers"]?["zemax-mcp"] : null;
            var args = entry?["args"] as JArray;
            var configuredToken = entry?["env"]?["ZEMAX_MCP_TOKEN"]?.ToString();
            var tokenMatches = string.IsNullOrWhiteSpace(expectedToken) ||
                string.Equals(configuredToken, expectedToken, StringComparison.Ordinal);
            return entry != null && string.Equals(Path.GetFileName(entry["command"]?.ToString()), "ZemaxMCP.ClientProxy.exe", StringComparison.OrdinalIgnoreCase) &&
                   args != null && args.Any(x => UrlsEqual(x?.ToString(), expectedUrl)) && tokenMatches;
        }
        catch { return false; }
    }
    private static bool IsCodexConfigured(string expectedUrl, string expectedToken)
    {
        try
        {
            if (!File.Exists(CodexPath)) return false;
            var match = Regex.Match(File.ReadAllText(CodexPath), @"(?ms)^\[mcp_servers\.zemax\]\s*(.*?)(?=^\[|\z)");
            if (!match.Success) return false;
            var url = Regex.Match(match.Groups[1].Value, "(?m)^url\\s*=\\s*[\"']([^\"']+)[\"']").Groups[1].Value;
            if (!UrlsEqual(url, expectedUrl)) return false;
            if (string.IsNullOrWhiteSpace(expectedToken)) return true;
            var authorization = Regex.Match(match.Groups[1].Value, "Authorization\\s*=\\s*[\"']Bearer\\s+([^\"']+)[\"']").Groups[1].Value;
            return string.Equals(authorization, expectedToken, StringComparison.Ordinal);
        }
        catch { return false; }
    }
    private static IEnumerable<string> GetVsCodeConfigPaths()
    {
        yield return VsCodeDefaultPath;
        var profiles = Path.Combine(AppData, "Code", "User", "profiles");
        if (!Directory.Exists(profiles)) yield break;
        string[] profileFolders;
        try { profileFolders = Directory.GetDirectories(profiles); }
        catch { yield break; }
        foreach (var folder in profileFolders) yield return Path.Combine(folder, "mcp.json");
    }
    private static bool UrlsEqual(string? left, string? right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) || !Uri.TryCreate(right, UriKind.Absolute, out var b)) return false;
        return a.AbsoluteUri.TrimEnd('/').Equals(b.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("The MCP endpoint must be an absolute HTTP or HTTPS address.", nameof(url));
    }
    private static string EscapeToml(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static void WriteAtomically(string path, string content)
    {
        var temporary = path + ".zemaxmcp-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            if (File.Exists(path))
            {
                var backup = path + ".zemaxmcp.bak";
                try { File.Replace(temporary, path, backup, true); }
                catch (PlatformNotSupportedException)
                {
                    File.Copy(path, backup, true);
                    File.Delete(path);
                    File.Move(temporary, path);
                }
            }
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }
}

internal sealed class ClientConfigurationStatus
{
    public ClientConfigurationStatus(string name, string[] aliases, bool detected, bool configured, string configPath, Action<string, string>? configure)
    { Name = name; Aliases = aliases; Detected = detected || configured; Configured = configured; ConfigPath = configPath; Configure = configure; }
    public string Name { get; }
    public string[] Aliases { get; }
    public bool Detected { get; }
    public bool Configured { get; }
    public string ConfigPath { get; }
    public Action<string, string>? Configure { get; }
}
