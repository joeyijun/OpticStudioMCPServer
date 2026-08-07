param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$hostProject = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj")
$hostSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\Program.cs")
$rpcClient = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\WorkerRpcClient.cs")
$workerSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Program.cs")
$workerRpc = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Rpc\WorkerRpcServer.cs")
$packages = Get-Content -Raw (Join-Path $root "Directory.Packages.props")

if ($hostProject -notmatch '<TargetFramework>net10\.0-windows</TargetFramework>' -or
    $hostProject -notmatch 'ModelContextProtocol\.AspNetCore') {
  throw "The public Host must target .NET 10 and use ModelContextProtocol.AspNetCore."
}
if ($packages -notmatch 'ModelContextProtocol\.AspNetCore" Version="2\.1\.0"') {
  throw "The official MCP ASP.NET Core SDK must be pinned to the current 2.1 stable release."
}
if ($hostSource -notmatch 'MapMcp\(' -or $hostSource -notmatch 'WithHttpTransport' -or
    $hostSource -match 'HttpListener|JsonRpcRequest') {
  throw "The Host must use the official ASP.NET Core MCP transport rather than a hand-written HTTP/JSON-RPC dispatcher."
}
if ($workerSource -match 'WithStdioServerTransport|WithStreamServerTransport|host\.RunAsync\(') {
  throw "The Worker must not own an MCP transport."
}
if ($workerRpc -notmatch 'ZemaxRpcProtocol\.InvokeTool' -or $workerRpc -notmatch 'CancellationTokenSource' -or
    $workerRpc -notmatch 'SemaphoreSlim _executionGate') {
  throw "The Worker RPC server must provide typed tool invocation, cancellation, and a single execution boundary."
}
if ($rpcClient -notmatch 'PipeSecurity' -or $rpcClient -notmatch 'ZEMAX_MCP_PIPE_SECRET' -or
    $rpcClient -notmatch 'CancelOperation') {
  throw "The Host-to-Worker channel must be ACL-protected, handshake-authenticated, and cancellation-aware."
}
if ($hostSource -notmatch 'OpticStudioControlLease' -or $hostSource -notmatch 'Mcp-Name') {
  throw "OpticStudio control ownership must be independent of MCP sessions and tied to a client identity."
}

dotnet build (Join-Path $root "src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj") -c $Configuration --nologo
Write-Host "Modern .NET 10 Host / private RPC boundary verification passed."
