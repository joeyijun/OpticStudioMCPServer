# OpticStudio MCP Server

**A GUI-first Windows HTTP MCP server for Zemax OpticStudio.** It detects installed OpticStudio versions, starts a resilient local or trusted-LAN MCP endpoint, and configures **Codex**, **Claude Desktop**, **Cursor**, **Kimi Code**, **WorkBuddy**, and **VS Code / GitHub Copilot** without manual configuration-file editing.

![Zemax MCP launcher dashboard](docs/images/launcher-dashboard.jpg)

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

For a single computer, the AI client uses the local MCP address. For two computers, enable **Share with a trusted LAN computer** on the OpticStudio computer, choose **Copy secure setup**, then paste the complete copied bundle into the single **Secure setup** field on the AI computer. It automatically extracts both the endpoint and token, stores the token with Windows user-scope encryption, and writes it into supported AI-client configurations automatically.

## Highlights

- **Graphical install and update** — `Install.exe` installs or updates the per-user application. `Portable-Install.cmd` is available where an organisation blocks the installer executable.
- **Resilient built-in HTTP MCP** — The .NET bridge applies request timeouts, tracks independent MCP sessions, and automatically recovers its server subprocess after an unexpected failure.
- **Authenticated LAN use** — Every launcher-managed request uses a random Bearer token. LAN listening is refused without a token, browser origins are restricted, and token rotation is one click.
- **Lens-change safety** — Read-only mode blocks mutating tools before ZOS-API access. In read/write mode, every recognised mutation first saves a timestamped copy of the current lens; a failed snapshot prevents the change.
- **Verified, recoverable updates** — Release metadata is RSA-signed, the ZIP size and SHA-256 are checked before extraction, and a separate updater restores the previous installation if replacement fails.
- **Dedicated ZOS-API thread** — All connection and tool operations are serialized on one long-lived STA thread to respect the COM threading model and avoid cross-thread session access.
- **Streamable HTTP lifecycle** — The built-in bridge validates MCP response negotiation, supports JSON and SSE responses, preserves MCP sessions, and supports `DELETE` session cleanup.
- **Long-job control** — POP, global search, and multistart optimization can return immediately with a job id. Use `zemax_job_status`, `zemax_job_list`, and `zemax_job_cancel` for queue position, live progress, result retrieval, and cooperative cancellation; the launcher also shows the active tool/job and elapsed time.
- **Per-client live dashboard** — Colour-coded cards distinguish installation, configuration, historical activity, and a real request made within the last five minutes. The launcher health check is excluded from AI activity.
- **Multi-version OpticStudio detection** — Detects classic Zemax and current `ANSYS Inc\v*` layouts from environment variables, both registry views, uninstall entries, and known Program Files locations. The launcher validates all three ZOS-API assemblies before offering a version.
- **One AI configuration menu** — Configure detected Codex, Claude Desktop, Cursor, Kimi Code, and WorkBuddy clients directly. VS Code / GitHub Copilot uses its native MCP review and trust flow. A generic HTTP MCP JSON entry covers other compatible agents.
- **Safe public package** — The ZIP does not redistribute proprietary ZOS-API DLLs. It uses the licensed OpticStudio installation at runtime on the Zemax computer.

## Connection modes

The server supports the following OpticStudio connection modes:

| Mode | Intended use |
|---|---|
| **Standalone** | Starts or controls an OpticStudio session for automated work. |
| **Extension** | Connects to an already-running OpticStudio session for interactive work. |

Use the launcher status dashboard to confirm that ZOS-API is loaded and OpticStudio is connected before asking the AI to work on a design.

For inspection-only sessions, enable **Read-only mode** before connecting the AI. In normal read/write mode, automatic lens snapshots are kept under `%LOCALAPPDATA%\ZemaxMCP\snapshots` (up to the newest 100 files). The status details show the active protection mode, snapshot folder, and most recent snapshot created in the current bridge session.

## Zemax discovery and license diagnostics

The dashboard reports the selected OpticStudio program folder, how it was discovered, the resolved `ZOSAPI.dll`, `ZOSAPI_Interfaces.dll`, and `ZOSAPI_NetHelper.dll` paths, the Zemax Data folder, and license status. After startup it separately reports each assembly's actual CLR load path, so “found” and “loaded” are independently visible on both computers. `ZOSAPI_NetHelper.dll` is supported both beside `ZOSAPI.dll` and in the newer `ZOS-API\Libraries` layout.

The Data folder is resolved from `ZEMAX_DATA_ROOT`, the OpticStudio user registry (`HKCU\SOFTWARE\Zemax\ZemaxRoot`), redirected Windows Documents, the normal `Documents\Zemax` location, or the OpticStudio Online default. The presence of `Data\License`, `Data\Configs\SNTLCONFIG.XML`, or `ANSYSLMD_LICENSE_FILE` is shown only as configuration evidence. The authoritative license result is reported after ZOS-API actually connects; secret environment-variable values are never written to logs.

For an unusual portable layout, set `ZEMAX_ROOT` to the program directory and optionally `ZEMAX_DATA_ROOT` to the Data directory before starting the launcher.

## AI client configuration

Use **Configure AI clients** in the launcher. Existing unrelated MCP entries are preserved and a backup is kept when an existing configuration is replaced. Supported HTTP clients receive both the endpoint and its Bearer header; Claude's packaged proxy receives the token without putting it in the server URL. If a token is rotated, reconfigure each client so its saved credential matches.

| Client | Configuration used by the launcher | Connection confirmation |
|---|---|---|
| Codex | `$CODEX_HOME/config.toml`, or `~/.codex/config.toml` | The client card turns green after an actual request. |
| Claude Desktop | `%APPDATA%/Claude/claude_desktop_config.json`; the packaged local stdio proxy reaches the HTTP/LAN endpoint | The client card turns green after an actual request. |
| Cursor | `~/.cursor/mcp.json` | The client card turns green after an actual request. |
| Kimi Code | `$KIMI_CODE_HOME/mcp.json`, or `~/.kimi-code/mcp.json` | Run `/mcp` in Kimi Code or watch the launcher client card. See the [official Kimi MCP guide](https://www.kimi.com/code/docs/en/kimi-code-cli/customization/mcp.html). |
| WorkBuddy | `~/.workbuddy/mcp.json` | WorkBuddy shows its own green/red MCP status; the launcher also records real requests. See the [official WorkBuddy MCP guide](https://www.workbuddy.ai/docs/zh/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/MCP-Guide). |
| VS Code / Copilot | Native `vscode:mcp/install` review flow; status checks the default and profile-specific `mcp.json` files | Approve the server in VS Code, then make a request. |

Each card displays the exact configuration path inspected by the launcher. “Configured” requires that the stored `zemax-mcp` entry matches the endpoint currently shown in the launcher; it does not claim that an AI process is connected. “Active now” is shown only when the bridge identifies that client and receives a request within five minutes. Client names depend on the identification string sent by each agent; unrecognised implementations appear as **Other MCP client**.

## MCP capabilities

The server exposes a broad tool set. AI clients discover the exact, version-matched schemas through MCP `tools/list`, so this README does not become a stale duplicate of the running server.

`zemax_get_system` reports both the ZOS-API system path and whether that file currently exists on disk, together with unsaved-change state, system mode, and system name. This distinguishes a named but not-yet-saved OpticStudio session from a loadable lens file.

Major groups include:

- System and file operations
- Lens Data Editor surfaces, fields, wavelengths, aperture, solves, and extra data
- Non-Sequential Component (NSC) objects and detectors
- Imaging and optical analyses: spot, MTF, PSF, POP, ray fans, aberrations, and illumination
- Optimization, merit functions, operands, constraints, and multistart jobs
- Multi-configuration, tolerance-data-editor (TDE), system settings, and glass-catalog operations
- Background job control for lengthy POP and optimization operations

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
| `zemax_get_system_metadata` / `zemax_set_system_metadata` | Read or update the system title, author, and notes without saving automatically. |
| `zemax_get_environment` / `zemax_set_environment` | Inspect or set temperature, pressure, and refractive-index environment adjustment. |
| `zemax_get_polarization` / `zemax_set_polarization` | Inspect or set polarization amplitudes, phases, method, and thin-film phase handling. |
| `zemax_get_units` | Inspect lens, analysis, source, MTF, and afocal unit settings. |
| `zemax_get_system_files` | Inspect selected coating, scatter, ABg, and GRIN files, with optional available-file lists. |
| `zemax_get_aperture_settings` | Inspect complete aperture, apodization, afocal/telecentric, and semi-diameter settings. |
| `zemax_get_advanced_system_settings` | Inspect OPD, paraxial-ray, F-number, Huygens, threading, and session-file settings. |
| `zemax_get_ray_aiming_settings` | Inspect pupil shifts, compression, cache, convergence, and enhanced/robust ray aiming. |
| `zemax_get_material_catalog_settings` | Inspect catalogs used by the system, with an optional list of all available catalogs. |
| `zemax_get_nonsequential_system_settings` | Inspect NSC ray limits, intensity thresholds, splitting, and source-file settings. |
| `zemax_get_stop_surface` / `zemax_set_stop_surface` | Inspect or change the sequential aperture-stop surface. |
| `zemax_get_first_order_data` | Calculate EFL, working F-numbers, paraxial image height, and magnification. |
| `zemax_get_vignetting` / `zemax_set_vignetting` / `zemax_clear_vignetting` | Inspect, calculate, or clear per-field vignetting factors. |
| `zemax_get_field_settings` | Inspect field type, normalization, comments, solves, activity, and vignetting factors. |
| `zemax_get_wavelength_settings` | Inspect wavelength values, weights, activity, and the actual primary wavelength. |
| `zemax_quick_focus` | Run Quick Focus with a selectable criterion and bounded timeout. |
| `zemax_scale_lens` | Scale a complete sequential lens by a positive factor or convert its physical units. |

The server also provides MCP resources for the current system, merit function, and operand documentation, plus prompt templates for design, optimisation, analysis, and design troubleshooting.

## Fork and attribution

This repository is a fork of the MIT-licensed **OpticStudio MCP Server** by Javier A Ruiz. This fork adds the packaged Windows launcher, GUI installer, built-in HTTP MCP bridge, trusted-LAN workflow, live status dashboard, graphical AI-client configuration, and additional acceptance tools.

The original copyright and MIT license are retained in [LICENSE](LICENSE). A valid Zemax OpticStudio licence is required to operate the server.

## Release maintainers

The public ZIP is produced on a trusted Windows computer with a licensed OpticStudio installation. The required ZOS-API files must remain outside source control and the public release. The build accepts `ZOSAPI_NetHelper.dll` either in the program root or `ZOS-API\Libraries`. Automatic-update releases also require the repository secret `UPDATE_SIGNING_PRIVATE_KEY_B64`; its public half is embedded in the launcher. See [Windows Quick Start](docs/QUICKSTART_WINDOWS.md#maintainers-publishing-an-update) for the release-runner requirements.
