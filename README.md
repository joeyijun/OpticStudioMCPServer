# OpticStudio MCP Server

**A GUI-first Windows HTTP MCP server for Zemax OpticStudio.** It detects installed OpticStudio versions, starts a resilient local or trusted-LAN MCP endpoint, and configures **Codex**, **Claude Desktop**, **Cursor**, **Kimi Code**, **WorkBuddy**, and **VS Code / GitHub Copilot** without manual configuration-file editing.

## Windows quick start

1. Download and extract `ZemaxMCP-win-x64.zip` from [Releases](../../releases).
2. Double-click **Install.exe**. It installs for the current Windows user, creates a desktop shortcut, and starts the launcher.
3. At first launch, accept the detected AI-client setup prompt, or choose **Configure AI clients** and select the desired client.
4. Restart the configured AI client once. The status dashboard distinguishes installed, configured, previously seen, and recently active clients.

The launcher refreshes status every 5 seconds. No Node.js, Supergateway, command line, source checkout, or manual ZOS-API DLL copying is needed for normal use.

For the complete one- and two-computer guide, see [Windows Quick Start](docs/QUICKSTART_WINDOWS.md).

## How it works

```mermaid
flowchart LR
  A["OpticStudio computer\nInstall.exe → Start-Zemax-MCP.exe"] --> B["Built-in HTTP MCP bridge\n/mcp"]
  B --> C["ZOS-API + licensed\nOpticStudio"]
  D["Codex / Claude / Cursor / Kimi / WorkBuddy / Copilot\nlocal or trusted LAN computer"] -->|"HTTP MCP"| B
  B --> E["Live dashboard\nMCP · ZOS-API · AI activity"]
```

For a single computer, the AI client uses the local MCP address. For two computers, enable **Share with a trusted LAN computer** on the OpticStudio computer, copy its address, and paste it into **Remote MCP address** on the AI-client computer.

## Highlights

- **Graphical install and update** — `Install.exe` installs or updates the per-user application. `Portable-Install.cmd` is available where an organisation blocks the installer executable.
- **Resilient built-in HTTP MCP** — The .NET bridge applies request timeouts, tracks independent MCP sessions, and automatically recovers its server subprocess after an unexpected failure.
- **Trusted LAN use** — Enable LAN sharing on the OpticStudio computer, copy its address, and configure the AI-client computer graphically.
- **Per-client live dashboard** — Colour-coded cards distinguish installation, configuration, historical activity, and a real request made within the last five minutes. The launcher health check is excluded from AI activity.
- **Multi-version OpticStudio detection** — Choose from detected installations; the launcher remembers the choice and can start at sign-in.
- **One AI configuration menu** — Configure detected Codex, Claude Desktop, Cursor, Kimi Code, and WorkBuddy clients directly. VS Code / GitHub Copilot uses its native MCP review and trust flow. A generic HTTP MCP JSON entry covers other compatible agents.
- **Safe public package** — The ZIP does not redistribute proprietary ZOS-API DLLs. It uses the licensed OpticStudio installation at runtime on the Zemax computer.

## Connection modes

The server supports the following OpticStudio connection modes:

| Mode | Intended use |
|---|---|
| **Standalone** | Starts or controls an OpticStudio session for automated work. |
| **Extension** | Connects to an already-running OpticStudio session for interactive work. |

Use the launcher status dashboard to confirm that ZOS-API is loaded and OpticStudio is connected before asking the AI to work on a design.

## AI client configuration

Use **Configure AI clients** in the launcher. Existing unrelated MCP entries are preserved and a backup is kept when an existing configuration is replaced.

| Client | Configuration used by the launcher | Connection confirmation |
|---|---|---|
| Codex | `~/.codex/config.toml` | The client card turns green after an actual request. |
| Claude Desktop | `%APPDATA%/Claude/claude_desktop_config.json` | The client card turns green after an actual request. |
| Cursor | `~/.cursor/mcp.json` | The client card turns green after an actual request. |
| Kimi Code | `$KIMI_CODE_HOME/mcp.json`, or `~/.kimi-code/mcp.json` | Run `/mcp` in Kimi Code or watch the launcher client card. See the [official Kimi MCP guide](https://www.kimi.com/code/docs/en/kimi-code-cli/customization/mcp.html). |
| WorkBuddy | `~/.workbuddy/mcp.json` | WorkBuddy shows its own green/red MCP status; the launcher also records real requests. See the [official WorkBuddy MCP guide](https://www.workbuddy.ai/docs/zh/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/MCP-Guide). |
| VS Code / Copilot | Native `vscode:mcp/install` review flow | Approve the server in VS Code, then make a request. |

“Configured” does not claim that an AI process is currently connected. “Active now” is shown only when the bridge identifies that client and receives a request within five minutes. Client names depend on the identification string sent by each agent; unrecognised implementations appear as **Other MCP client**.

## MCP capabilities

The server exposes a broad tool set. AI clients discover the exact, version-matched schemas through MCP `tools/list`, so this README does not become a stale duplicate of the running server.

Major groups include:

- System and file operations
- Lens Data Editor surfaces, fields, wavelengths, aperture, solves, and extra data
- Non-Sequential Component (NSC) objects and detectors
- Imaging and optical analyses: spot, MTF, PSF, POP, ray fans, aberrations, and illumination
- Optimization, merit functions, operands, constraints, and multistart jobs
- Multi-configuration, tolerance-data-editor (TDE), system settings, and glass-catalog operations

This fork additionally includes the following acceptance and validation tools:

| Tool | Purpose |
|---|---|
| `zemax_set_surface_aperture` / `zemax_get_surface_aperture` | Set or inspect real circular apertures and obscurations. |
| `zemax_set_off_axis_conic` | Read or set Off-Axis Conic Freeform offset and normalization radius. |
| `zemax_get_global_matrix` | Read a surface local-to-global rotation matrix and vertex origin. |
| `zemax_aperture_throughput` | Sample pupil throughput and identify vignette surfaces. |
| `zemax_ray_trace_extended` | Trace a real ray with intercept, direction, intensity, error, and vignette data. |

### Current release additions

The current Windows release also adds the following practical inspection and system-setting tools. They are registered and verified by the release build; clients should still use `tools/list` as the authoritative schema source for the installed OpticStudio version.

| Tool | Purpose |
|---|---|
| `zemax_get_nsc_objects` | List NSC objects with type, material, hierarchy, activity, and position data. |
| `zemax_get_nsc_detector` | Inspect an NSC detector's dimensions and display mode. |
| `zemax_get_nsc_object_parameters` | Read type-specific parameters for an NSC object. |
| `zemax_get_tolerances` | Read Tolerance Data Editor operands safely, including unset bounds. |
| `zemax_set_number_of_fields` / `zemax_set_number_of_wavelengths` | Resize the system field or wavelength lists. |
| `zemax_get_apodization` / `zemax_set_apodization` | Inspect or set pupil apodization type and factor. |
| `zemax_get_clear_semi_diameter_margin` / `zemax_set_clear_semi_diameter_margin` | Inspect or set Clear Semi-Diameter Margin in millimetres and percent. Availability depends on the installed ZOS-API version. |
| `zemax_get_mtf_units` / `zemax_set_mtf_units` | Inspect or set MTF units in cycles per millimetre or milliradian. |

The server also provides MCP resources for the current system, merit function, and operand documentation, plus prompt templates for design, optimisation, analysis, and design troubleshooting.

## Fork and attribution

This repository is a fork of the MIT-licensed **OpticStudio MCP Server** by Javier A Ruiz. This fork adds the packaged Windows launcher, GUI installer, built-in HTTP MCP bridge, trusted-LAN workflow, live status dashboard, graphical AI-client configuration, and additional acceptance tools.

The original copyright and MIT license are retained in [LICENSE](LICENSE). A valid Zemax OpticStudio licence is required to operate the server.

## Release maintainers

The public ZIP is produced on a trusted Windows computer with a licensed OpticStudio installation. The required ZOS-API files must remain outside source control and the public release. See [Windows Quick Start](docs/QUICKSTART_WINDOWS.md#maintainers-publishing-an-update) for the release-runner requirements.
