# Zemax MCP Windows quick start

1. Download and extract `ZemaxMCP-win-x64.zip` on the computer that has OpticStudio installed.
2. Double-click **Install.exe**. It installs into your user profile, creates a **Start Zemax MCP** desktop shortcut, and starts the service. No command line or administrator permission is needed for a local connection.
3. On later use, double-click **Start-Zemax-MCP.exe** (or the desktop shortcut). The newest detected OpticStudio version is selected and the local endpoint starts automatically; the window lets you switch versions if needed. A second launch simply shows that the app is already running.
4. At first launch, confirm the detected AI-client setup prompt. Alternatively, use **Configure AI clients** and choose Codex, Claude Desktop, Cursor, Kimi Code, WorkBuddy, or VS Code / Copilot. Restart the configured AI client once.
5. The dashboard checks MCP, ZOS-API/OpticStudio, and each AI client's activity automatically every 5 seconds. **Refresh** is available for an immediate check.

The client cards deliberately use different states: **Installed** means the application was detected, **Configured** means its settings contain the Zemax endpoint, **Last seen** means it previously sent a request, and **Active now** means it made a real MCP request within five minutes. The launcher's own Test button never counts as AI activity.

If your organization blocks `Install.exe` but permits batch files, double-click `Portable-Install.cmd` instead. It performs the same per-user copy and starts the launcher. This is a convenience fallback, not a way to bypass corporate security policy: if Windows also blocks the application executable, request an IT allow-list or a company code-signing certificate.

For a second PC, tick **Share with a trusted LAN computer**, then use the displayed LAN endpoint in the client configuration. If Windows Firewall asks, allow the selected port only for the trusted private network. Do not expose an unauthenticated MCP endpoint to the public internet.

On the AI-client computer, extract the same release, double-click `Install.exe`, paste the endpoint copied from the OpticStudio computer into **Remote MCP address**, then use **Configure AI clients**. This is the only extra step for a two-computer setup and requires no terminal or manual file editing.

Kimi Code uses `~/.kimi-code/mcp.json` (or `$KIMI_CODE_HOME/mcp.json`) and WorkBuddy uses `~/.workbuddy/mcp.json`. The launcher edits only its `zemax-mcp` entry and preserves other servers. For another HTTP-capable agent, choose **Copy generic HTTP MCP JSON** and paste the result into that agent's documented MCP settings.

Claude Desktop is configured through the packaged `ZemaxMCP.ClientProxy.exe`. Claude starts this small local stdio process, which forwards requests to the HTTP endpoint shown in the launcher; this also works when OpticStudio is on the second computer. Codex honors `$CODEX_HOME`, Kimi honors `$KIMI_CODE_HOME`, and the launcher displays the exact configuration file inspected for every supported client. A client is marked **Configured** only when that file points to the current endpoint.

## Zemax paths and license status

The live status area shows the detected OpticStudio directory, discovery source, candidate ZOS-API files, each assembly's actual CLR load path after startup, Data directory, and runtime license result. Current Ansys layouts with `ZOSAPI_NetHelper.dll` under `ZOS-API\Libraries` are supported as well as classic installations with all three files in the program directory.

The launcher checks `ZEMAX_ROOT`, Windows installation/product registry entries, classic Zemax folders, and versioned `Program Files\ANSYS Inc\v*` folders. For the Data directory it checks `ZEMAX_DATA_ROOT`, OpticStudio's `HKCU\SOFTWARE\Zemax\ZemaxRoot` setting, redirected Documents, and the default `Documents\Zemax` folder. License folders and environment settings are diagnostic clues only; a valid/invalid result appears only after ZOS-API creates an OpticStudio application. No license-server value is printed to logs.

## Updates and logs

Use **Updates** in the launcher to download and apply the current GitHub release, then restart it. Each release includes the server, HTTP bridge, launcher, and installer. Logs are created in the application's `logs` folder; your OpticStudio installation and client configuration are retained. The launcher retries its bridge after an unexpected exit, while the bridge separately restarts a failed MCP server subprocess and reports restart/error information in the dashboard.

The public ZIP intentionally contains no `ZOSAPI*.dll` files. On the computer that has OpticStudio installed, the launcher uses that user's local licensed installation at runtime; it does not download, bundle, or redistribute Ansys ZOS-API files.

### Maintainers: publishing an update

The full ZIP is published from a trusted self-hosted Windows GitHub Actions runner that has a licensed OpticStudio installation. Give that runner the labels `self-hosted`, `windows`, and `zemax`, and set its machine-level `ZEMAX_ROOT` environment variable to the OpticStudio directory. The publishing script accepts NetHelper in either the program root or `ZOS-API\Libraries`. Pushing a `v*` tag then creates the GitHub Release and uploads `ZemaxMCP-win-x64.zip`; installed launchers will discover it through **Check updates**. This keeps proprietary ZOS-API files out of source control and public hosted runners.

Use **Open logs** in the launcher for both bridge and server diagnostics; no command prompt is needed to locate or inspect logs.

## Development builds

Release maintainers create the ZIP on a Windows computer that has OpticStudio installed (the ZOS-API DLLs are proprietary and cannot be built on GitHub-hosted runners). Set `ZEMAX_ROOT` to the OpticStudio folder, then run:

```powershell
./scripts/publish-windows.ps1
```

The script writes `artifacts/ZemaxMCP-win-x64.zip`.
