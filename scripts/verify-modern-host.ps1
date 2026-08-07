param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$hostProject = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj")
$hostSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\Program.cs")
$hostOptions = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\HostOptions.cs")
$rpcClient = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\WorkerRpcClient.cs")
$workerSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Program.cs")
$workerRpc = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Rpc\WorkerRpcServer.cs")
$workerProject = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\ZemaxMCP.Server.csproj")
$workerTools = Get-ChildItem (Join-Path $root "src\ZemaxMCP.Server\Tools") -Recurse -Filter *.cs | ForEach-Object { Get-Content -Raw $_.FullName } | Out-String
$privateRpcTest = Get-Content -Raw (Join-Path $root "tests\ZemaxMCP.PrivateRpcTests\Program.cs")
$schemaTest = Get-Content -Raw (Join-Path $root "tests\ZemaxMCP.PrivateRpcTests\WorkerToolRegistrySchemaAssertions.cs")
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
if ($workerSource -match 'WithStdioServerTransport|WithStreamServerTransport|AddMcpServer|host\.RunAsync\(' -or
    $workerRpc -match 'ModelContextProtocol|McpServerTool|RequestContext' -or
    $workerProject -match 'PackageReference Include="ModelContextProtocol"' -or
    $workerTools -match 'ModelContextProtocol|McpServerTool') {
  throw "The Worker must not own an MCP transport."
}
if ($workerRpc -notmatch 'ZemaxRpcProtocol\.InvokeTool' -or $workerRpc -notmatch 'CancellationTokenSource' -or
    $workerRpc -notmatch 'WorkerToolRegistry' -or
    $workerRpc -notmatch 'SemaphoreSlim _executionGate') {
  throw "The Worker RPC server must provide protocol-neutral tool invocation, cancellation, and a single execution boundary."
}
if ($rpcClient -notmatch 'PipeSecurity' -or $rpcClient -notmatch 'ZEMAX_MCP_PIPE_SECRET' -or
    $rpcClient -notmatch 'CancelOperation') {
  throw "The Host-to-Worker channel must be ACL-protected, handshake-authenticated, and cancellation-aware."
}
if ($hostSource -notmatch 'OpticStudioControlLease' -or $hostSource -notmatch 'ResolveControlIdentity' -or
    $hostSource -match 'AllowAnyOrigin\(' -or $hostSource -notmatch 'zemax-mcp-remote-endpoint' -or
    $hostSource -match 'zemax-mcp-client-name|Mcp-Version' -or $hostSource -notmatch 'UseSetting\("AllowedHosts"' -or $hostOptions -notmatch 'allowed-origin') {
  throw "Control ownership must use an authenticated profile or client-info plus remote endpoint; Host and Origin allow-lists must not be wildcarded."
}
if ($rpcClient -notmatch 'HardRecoveryTimeoutSeconds' -or $rpcClient -notmatch 'FaultWorkerConnection' -or
    $rpcClient -notmatch 'CancelOperation' -or $rpcClient -notmatch 'CancellationWriteTimeoutSeconds' -or
    $rpcClient -notmatch 'RequestWriteTimeoutSeconds' -or $rpcClient -notmatch 'hardDeadline' -or
    $rpcClient -notmatch 'RecoverCancelledOperationAsync') {
  throw "Worker RPC must retain bounded request/cancellation writes, soft cancellation, hard recovery, and one fault-recovery path."
}
$callToolBody = [regex]::Match($rpcClient,
    'public async Task<CallToolResult> CallToolAsync[\s\S]*?(?<body>\{[\s\S]*?)\r?\n    public async Task<WorkerStatus>').Groups['body'].Value
if ([string]::IsNullOrWhiteSpace($callToolBody) -or
    $callToolBody.IndexOf('_executionGate.WaitAsync', [StringComparison]::Ordinal) -lt 0 -or
    $callToolBody.IndexOf('WaitForCancelledOperationRecoveryAsync', [StringComparison]::Ordinal) -lt 0 -or
    $callToolBody.IndexOf('_executionGate.WaitAsync', [StringComparison]::Ordinal) -gt $callToolBody.IndexOf('WaitForCancelledOperationRecoveryAsync', [StringComparison]::Ordinal)) {
  throw "Tool execution must acquire the Host execution gate before checking cancelled-operation recovery so queued calls cannot enter a draining Worker generation."
}
if ($privateRpcTest -notmatch '2026-07-28' -or $privateRpcTest -notmatch 'io\.modelcontextprotocol/clientInfo' -or
    $privateRpcTest -notmatch 'Mcp-Method' -or $privateRpcTest -match '"initialize"') {
  throw "The private RPC E2E test must exercise a stateless 2026 MCP tools/call with request-scoped clientInfo and no initialize step."
}
if ($schemaTest -notmatch 'ModuleInitializer' -or $schemaTest -notmatch 'zemax_test_schema_contract' -or
    $schemaTest -notmatch 'cancellationToken' -or $schemaTest -notmatch 'additionalProperties') {
  throw "Worker tool schema regressions must verify required parameters, cancellation exclusion, nested records, enums, and dictionaries."
}

dotnet build (Join-Path $root "src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Modern Host build failed." }
dotnet run --project (Join-Path $root "tests\ZemaxMCP.PrivateRpcTests\ZemaxMCP.PrivateRpcTests.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Private Host-to-Worker RPC integration verification failed." }
Write-Host "Modern .NET 10 Host / private RPC boundary verification passed."
