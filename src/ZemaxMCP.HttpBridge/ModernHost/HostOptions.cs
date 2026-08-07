using System.Globalization;

namespace ZemaxMCP.HttpBridge.ModernHost;

internal sealed class HostOptions
{
    public string WorkerPath { get; private set; } = Path.Combine(AppContext.BaseDirectory, "ZemaxMCP.Worker.exe");
    public string ZemaxRoot { get; private set; } = string.Empty;
    public string Host { get; private set; } = "127.0.0.1";
    public int Port { get; private set; } = 8000;
    public string McpPath { get; private set; } = "/mcp";
    public string LogDirectory { get; private set; } = Path.Combine(AppContext.BaseDirectory, "logs");
    public string AccessToken { get; private set; } = Environment.GetEnvironmentVariable("ZEMAX_MCP_TOKEN") ?? string.Empty;
    public int WorkerStartupTimeoutSeconds { get; private set; } = 90;
    public int RequestTimeoutSeconds { get; private set; } = 300;
    public int RequestWriteTimeoutSeconds { get; private set; } = 10;
    public int HardRecoveryTimeoutSeconds { get; private set; } = 360;
    public int CancellationWriteTimeoutSeconds { get; private set; } = 5;
    public bool ReadOnly { get; private set; }
    public string Toolset { get; private set; } = "full-expert";
    public string SnapshotDirectory { get; private set; } = Environment.GetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "snapshots");
    private readonly List<OriginRule> _allowedOrigins = new();
    private readonly List<string> _allowedHosts = new();
    public IReadOnlyList<OriginRule> AllowedOrigins => _allowedOrigins;
    public IReadOnlyList<string> AllowedHosts => _allowedHosts;

    public static HostOptions Parse(string[] args)
    {
        var options = new HostOptions();
        if (args.Length % 2 != 0) throw new ArgumentException("Host arguments must be supplied as --option value pairs.");

        for (var i = 0; i < args.Length; i += 2)
        {
            var option = args[i].ToLowerInvariant();
            var value = args[i + 1];
            switch (option)
            {
                // Keep --server compatible with existing Launcher releases.
                case "--server": options.WorkerPath = value; break;
                case "--worker": options.WorkerPath = value; break;
                case "--zemax-root": options.ZemaxRoot = value; break;
                case "--host": options.Host = value; break;
                case "--port": options.Port = ParseRange(value, option, 1, 65535); break;
                case "--path": options.McpPath = NormalizePath(value); break;
                case "--log-dir": options.LogDirectory = value; break;
                case "--worker-startup-timeout-seconds": options.WorkerStartupTimeoutSeconds = ParseRange(value, option, 10, 600); break;
                case "--request-timeout-seconds": options.RequestTimeoutSeconds = ParseRange(value, option, 10, 3600); break;
                case "--request-write-timeout-seconds": options.RequestWriteTimeoutSeconds = ParseRange(value, option, 1, 60); break;
                case "--hard-recovery-timeout-seconds": options.HardRecoveryTimeoutSeconds = ParseRange(value, option, 20, 7200); break;
                case "--cancellation-write-timeout-seconds": options.CancellationWriteTimeoutSeconds = ParseRange(value, option, 1, 30); break;
                case "--allowed-origin": options._allowedOrigins.Add(OriginRule.Parse(value)); break;
                case "--allowed-host": options._allowedHosts.Add(ParseHost(value, option)); break;
                case "--read-only": options.ReadOnly = ParseBoolean(value, option); break;
                case "--toolset": options.Toolset = value; break;
                case "--snapshot-dir": options.SnapshotDirectory = value; break;
                // The superseded bridge test switches are intentionally rejected
                // so test-only faults cannot leak into product execution.
                default: throw new ArgumentException("Unknown Host option: " + args[i]);
            }
        }

        if (string.IsNullOrWhiteSpace(options.WorkerPath)) throw new ArgumentException("--worker cannot be empty.");
        if (string.IsNullOrWhiteSpace(options.Host)) throw new ArgumentException("--host cannot be empty.");
        if (options.HardRecoveryTimeoutSeconds <= options.RequestTimeoutSeconds)
            throw new ArgumentException("--hard-recovery-timeout-seconds must be greater than --request-timeout-seconds.");
        if (options.Host == "0.0.0.0" && string.IsNullOrWhiteSpace(options.AccessToken))
            throw new ArgumentException("LAN sharing requires ZEMAX_MCP_TOKEN to be configured.");
        if (options._allowedHosts.Count == 0)
        {
            if (IsWildcardBind(options.Host))
                throw new ArgumentException("A wildcard --host requires at least one explicit --allowed-host.");
            options._allowedHosts.Add(ParseHost(options.Host, "--host"));
            if (IsLoopback(options.Host))
            {
                options._allowedHosts.Add("localhost");
                options._allowedHosts.Add("127.0.0.1");
                options._allowedHosts.Add("[::1]");
            }
        }
        if (options._allowedOrigins.Count == 0 && IsLoopback(options.Host))
        {
            options._allowedOrigins.Add(OriginRule.AnyPort("http", "127.0.0.1"));
            options._allowedOrigins.Add(OriginRule.AnyPort("http", "localhost"));
            options._allowedOrigins.Add(OriginRule.AnyPort("http", "::1"));
        }
        return options;
    }

    private static int ParseRange(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum || parsed > maximum)
            throw new ArgumentException(option + " must be between " + minimum + " and " + maximum + ".");
        return parsed;
    }

    private static bool ParseBoolean(string value, string option)
    {
        if (!bool.TryParse(value, out var parsed)) throw new ArgumentException(option + " must be true or false.");
        return parsed;
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("--path cannot be empty.");
        return "/" + value.Trim().Trim('/');
    }

    private static string ParseHost(string value, string option)
    {
        var host = value.Trim();
        if (string.IsNullOrWhiteSpace(host) || host.Contains('*') || host.Contains('/') || (host.Contains(':') && !host.StartsWith("[", StringComparison.Ordinal)))
            throw new ArgumentException(option + " must be a concrete host name or bracketed IPv6 address.");
        return host;
    }

    private static bool IsWildcardBind(string host) => host is "0.0.0.0" or "::" or "[::]";
    private static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host is "::1" or "[::1]";
}

internal sealed record OriginRule(string Scheme, string Host, int? Port)
{
    public static OriginRule Parse(string value)
    {
        var raw = value.Trim();
        var anyPort = raw.EndsWith(":*", StringComparison.Ordinal);
        if (anyPort) raw = raw.Substring(0, raw.Length - 2);
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0))
            throw new ArgumentException("--allowed-origin must be an absolute origin, optionally ending in :*.");
        return new OriginRule(uri.Scheme, uri.Host, anyPort ? null : uri.Port);
    }

    public static OriginRule AnyPort(string scheme, string host) => new(scheme, host, null);

    public bool Matches(Uri origin) =>
        string.Equals(Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
        (!Port.HasValue || Port.Value == origin.Port);
}
