using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public class ConnectTool
{
    private readonly IZemaxSession _session;

    public ConnectTool(IZemaxSession session) => _session = session;

    public record ConnectResult(
        bool Success,
        string? Error,
        bool IsConnected,
        string? CurrentFile,
        string? Mode
    );

    [ZemaxTool(Name = "zemax_connect")]
    [Description("Connect to Zemax OpticStudio. Modes: 'standalone' (default, launches an automated instance) or 'extension' (connect to a running OpticStudio UI with Programming > Interactive Extension enabled). A call switches modes or extension instance IDs when necessary.")]
    public async Task<ConnectResult> ExecuteAsync(
        [Description("Connection mode: 'standalone' or 'extension'. Default: standalone.")]
        string mode = "standalone",
        [Description("OpticStudio instance ID for extension mode. Use 0 for the first available instance. Ignored in standalone mode.")]
        int instanceId = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mode))
                throw new ArgumentException("Connection mode is required.", nameof(mode));
            if (instanceId < 0)
                throw new ArgumentOutOfRangeException(nameof(instanceId), "Instance ID cannot be negative.");

            var connectionMode = mode.Trim().ToLowerInvariant() switch
            {
                "standalone" => ConnectionMode.Standalone,
                "extension" => ConnectionMode.Extension,
                _ => throw new ArgumentException($"Invalid mode '{mode}'. Use 'standalone' or 'extension'.")
            };
            var targetInstanceId = connectionMode == ConnectionMode.Standalone ? 0 : instanceId;

            if (_session.IsConnecting)
                await _session.WaitForBackgroundConnectAsync(cancellationToken);

            if (_session.IsConnected &&
                _session.CurrentMode == connectionMode &&
                _session.CurrentInstanceId == targetInstanceId)
            {
                return Result(success: true);
            }

            if (_session.IsConnected)
                await _session.DisconnectAsync();

            var connected = await _session.ConnectAsync(connectionMode, targetInstanceId, cancellationToken);
            return Result(connected, connected ? null : "Failed to connect to OpticStudio");
        }
        catch (Exception ex)
        {
            return Result(success: false, error: ex.Message);
        }
    }

    [ZemaxTool(Name = "zemax_status")]
    [Description("Get the current OpticStudio connection status, including the actual connection mode.")]
    public Task<ConnectResult> GetStatusAsync() => Task.FromResult(Result(_session.IsConnected));

    [ZemaxTool(Name = "zemax_disconnect")]
    [Description("Disconnect the Worker from OpticStudio. A standalone application owned by the Worker is closed; an Interactive Extension connection is detached.")]
    public async Task<ConnectResult> DisconnectAsync()
    {
        try
        {
            await _session.DisconnectAsync();
            return Result(success: true);
        }
        catch (Exception ex)
        {
            return Result(success: false, error: ex.Message);
        }
    }

    [ZemaxTool(Name = "zemax_restart")]
    [Description("Restart the OpticStudio connection using the configured default connection mode.")]
    public async Task<ConnectResult> RestartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_session.IsConnected)
                await _session.DisconnectAsync();

            await Task.Delay(500, cancellationToken);
            var connected = await _session.ConnectAsync(cancellationToken);
            return Result(connected, connected ? null : "Failed to reconnect to OpticStudio");
        }
        catch (Exception ex)
        {
            return Result(success: false, error: ex.Message);
        }
    }

    private ConnectResult Result(bool success, string? error = null) => new(
        Success: success,
        Error: error,
        IsConnected: _session.IsConnected,
        CurrentFile: _session.CurrentFilePath,
        Mode: _session.CurrentMode?.ToString());
}
