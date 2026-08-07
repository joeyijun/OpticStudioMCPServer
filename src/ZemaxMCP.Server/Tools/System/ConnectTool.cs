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
    [Description("Connect to Zemax OpticStudio. Modes: 'standalone' (default, launches headless instance) or 'extension' (connect to running OpticStudio with UI - requires Programming > Interactive Extension enabled in OpticStudio). A call switches modes or extension instance IDs when necessary.")]
    public async Task<ConnectResult> ExecuteAsync(
        [Description("Connection mode: 'standalone' (headless, no UI) or 'extension' (attach to running OpticStudio with UI). Default: standalone.")]
        string mode = "standalone",
        [Description("OpticStudio instance ID for extension mode. Use 0 for the first available instance. Only used when mode is 'extension'.")]
        int instanceId = 0)
    {
        try
        {
            var connectionMode = mode.ToLowerInvariant() switch
            {
                "standalone" => ConnectionMode.Standalone,
                "extension" => ConnectionMode.Extension,
                _ => throw new ArgumentException($"Invalid mode '{mode}'. Use 'standalone' or 'extension'.")
            };

            // The Worker begins a standalone connection in the background. Wait
            // for it before deciding whether the caller wants the same target or
            // a mode/instance switch.
            if (_session.IsConnecting)
            {
                await _session.WaitForBackgroundConnectAsync();
            }

            if (_session.IsConnected &&
                _session.CurrentMode == connectionMode &&
                _session.CurrentInstanceId == instanceId)
            {
                return Result(success: true);
            }

            if (_session.IsConnected)
            {
                await _session.DisconnectAsync();
            }

            var connected = await _session.ConnectAsync(connectionMode, instanceId);
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
    [Description("Disconnect from Zemax OpticStudio and close the application. Use this to cleanly close the session.")]
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
    [Description("Restart the Zemax OpticStudio connection using the configured default connection mode.")]
    public async Task<ConnectResult> RestartAsync()
    {
        try
        {
            if (_session.IsConnected)
            {
                await _session.DisconnectAsync();
            }

            await Task.Delay(500);
            var connected = await _session.ConnectAsync();
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
