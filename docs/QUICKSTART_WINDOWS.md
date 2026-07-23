# Zemax MCP Windows quick start

1. Download and extract `ZemaxMCP-win-x64.zip` from Releases on the computer that has OpticStudio installed.
2. Start **Zemax MCP Setup.exe**. It detects every local OpticStudio installation containing the required ZOS-API files.
3. Select the intended version. Start the built-in HTTP endpoint (default `http://<PC-IP>:8000/mcp`). No Node.js or Supergateway is required.
4. Select Codex, Claude, or Cursor to add a `zemax-mcp` remote MCP entry automatically.

For a second PC, listen on `0.0.0.0`, allow the selected TCP port only from the trusted LAN computer in Windows Firewall, and configure the client with the host computer's fixed LAN address. Do not expose an unauthenticated MCP endpoint to the public internet.

## Updates and logs

The launcher downloads and applies the current signed-off GitHub release with **Check updates**, then restarts itself. Releases are built automatically from version tags and include the server, HTTP bridge, launcher, and a rolling `logs` folder. Your OpticStudio installation and client configuration are retained.

## Development builds

Create `ZemaxPaths.props` with `ZEMAX_ROOT` set to an OpticStudio folder (or a copied, compatible ZOS-API folder), then run:

```powershell
./scripts/publish-windows.ps1
```

The script writes `artifacts/ZemaxMCP-win-x64.zip`.
