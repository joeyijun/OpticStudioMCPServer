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
    public int HardRecoveryTimeoutSeconds { get; private set; } = 360;
    public bool ReadOnly { get; private set; }
    public string Toolset { get; private set; } = "full-expert";
    public string SnapshotDirectory { get; private set; } = Environment.GetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "snapshots");

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
                case "--hard-recovery-timeout-seconds": options.HardRecoveryTimeoutSeconds = ParseRange(value, option, 20, 7200); break;
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
}
