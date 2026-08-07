param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$hostProject = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj")
$hostSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\Program.cs")
$hostOptions = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\HostOptions.cs")
$originPolicy = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\OriginPolicy.cs")
$rpcClient = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.HttpBridge\ModernHost\WorkerRpcClient.cs")
$rpcProtocol = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Rpc\Protocol\ZemaxRpcProtocol.cs")
$rpcContracts = Get-ChildItem (Join-Path $root "src\ZemaxMCP.Rpc\Contracts") -Filter *.cs | ForEach-Object { Get-Content -Raw $_.FullName } | Out-String
$workerSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Program.cs")
$bootstrapSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\BootstrapProgram.cs")
$workerRpc = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Rpc\WorkerRpcServer.cs")
$workerProject = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\ZemaxMCP.Server.csproj")
$workerRegistry = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Tooling\ZemaxToolAttributes.cs")
$workerTools = Get-ChildItem (Join-Path $root "src\ZemaxMCP.Server\Tools") -Recurse -Filter *.cs | ForEach-Object { Get-Content -Raw $_.FullName } | Out-String
$setSurfaceSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.Server\Tools\LensData\SetSurfaceTool.cs")
$manifestProject = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.ToolManifest\ZemaxMCP.ToolManifest.csproj")
$manifestSource = Get-Content -Raw (Join-Path $root "src\ZemaxMCP.ToolManifest\ToolManifestEntry.cs")
$generatorSource = Get-Content -Raw (Join-Path $root "tools\ZemaxMCP.ToolManifestGenerator\Program.cs")
$privateRpcTest = Get-Content -Raw (Join-Path $root "tests\ZemaxMCP.PrivateRpcTests\Program.cs")
$schemaTest = Get-Content -Raw (Join-Path $root "tests\ZemaxMCP.PrivateRpcTests\StaticToolManifestAssertions.cs")
$liveVerifier = Get-Content -Raw (Join-Path $root "scripts\verify-live-mcp.ps1")
$packages = Get-Content -Raw (Join-Path $root "Directory.Packages.props")

# Parse the manual/live acceptance harness during CI even though the hosted
# runner cannot execute it against proprietary ZOS-API. This catches broken
# PowerShell edits before a maintainer reaches the OpticStudio test machine.
[scriptblock]::Create($liveVerifier) | Out-Null

if ($hostProject -notmatch '<TargetFramework>net10\.0-windows</TargetFramework>' -or
    $hostProject -notmatch 'ModelContextProtocol\.AspNetCore') {
  throw "The public Host must target .NET 10 and use ModelContextProtocol.AspNetCore."
}
if ($hostProject -notmatch 'InternalsVisibleTo Include="ZemaxMCP\.PrivateRpcTests"' -or
    (Test-Path (Join-Path $root "src\ZemaxMCP.HttpBridge\Properties\AssemblyInfo.cs"))) {
  throw "SDK-style Host assembly metadata must stay in the project file; the redundant hand-written AssemblyInfo must not return."
}
if ($packages -notmatch 'ModelContextProtocol\.AspNetCore" Version="2\.1\.0"' -or
    $packages -notmatch 'Microsoft\.CodeAnalysis\.CSharp" Version="5\.6\.0"') {
  throw "The Host SDK and build-time manifest generator dependencies must remain pinned to verified stable versions."
}
if ($hostSource -notmatch 'MapMcp\(' -or $hostSource -notmatch 'WithHttpTransport' -or
    $hostSource -match 'HttpListener|JsonRpcRequest') {
  throw "The Host must use the official ASP.NET Core MCP transport rather than a hand-written HTTP/JSON-RPC dispatcher."
}
if ($hostSource -notmatch 'toolset = options\.Toolset') {
  throw "Structured Host health must report the active toolset so live release verification can interpret the policy-visible catalogue."
}
if ($workerSource -match 'WithStdioServerTransport|WithStreamServerTransport|AddMcpServer|host\.RunAsync\(' -or
    $workerRpc -match 'ModelContextProtocol|McpServerTool|RequestContext' -or
    $workerProject -match 'PackageReference Include="ModelContextProtocol"' -or
    $workerTools -match 'ModelContextProtocol|McpServerTool') {
  throw "The Worker must not own an MCP transport."
}
if ($bootstrapSource -notmatch 'AssemblyResolve' -or $bootstrapSource -notmatch 'CandidateFolders' -or
    $workerSource -match 'AssemblyResolve|Assembly\.LoadFrom') {
  throw "ZOS-API CLR binding must have one owner in BootstrapProgram; ServerApplication must not register a duplicate resolver."
}
if ((Test-Path (Join-Path $root "src\ZemaxMCP.Server\Prompts")) -or
    (Test-Path (Join-Path $root "src\ZemaxMCP.Server\Resources")) -or
    (Test-Path (Join-Path $root "src\ZemaxMCP.Server\appsettings.json")) -or
    $workerProject -match 'Configuration\.Json|System\.Management|Serilog\.Sinks\.Console|Compile Remove="Prompts|appsettings\.json' -or
    $packages -match 'Microsoft\.Extensions\.Configuration\.Json|System\.Management|Serilog\.Sinks\.Console|System\.Diagnostics\.DiagnosticSource|System\.Buffers|System\.Memory|System\.Numerics\.Vectors|System\.Runtime\.CompilerServices\.Unsafe|PackageVersion Include="Serilog"|PackageVersion Include="Microsoft\.Extensions\.Logging"') {
  throw "Retired Worker prompt/resource/configuration sources and obsolete direct package pins must not return."
}
if ($workerRpc -notmatch 'ZemaxRpcProtocol\.InvokeTool' -or $workerRpc -notmatch 'CancellationTokenSource' -or
    $workerRpc -notmatch 'WorkerToolRegistry' -or $workerRpc -notmatch 'SemaphoreSlim _executionGate' -or
    $workerRpc -notmatch 'StaticToolManifest\.IsAllowed') {
  throw "The Worker RPC server must provide protocol-neutral invocation, cancellation, serialization, and manifest-backed execution admission."
}
if ($rpcProtocol -notmatch 'public const int Version = 3;' -or
    $rpcProtocol -notmatch 'WorkerHandshake' -or $rpcProtocol -notmatch 'ManifestFingerprint' -or
    $rpcProtocol -match 'GetToolCatalog' -or $rpcContracts -match 'ToolCatalogRequest|ToolInvocationResult') {
  throw "Private RPC v3 must remove Worker-owned discovery/obsolete contracts and negotiate the manifest fingerprint."
}
if ($manifestProject -notmatch 'GenerateStaticToolManifest' -or $manifestProject -notmatch 'ZemaxMCP\.ToolManifestGenerator' -or
    $manifestSource -notmatch 'StaticToolManifest' -or $manifestSource -notmatch 'ContractFingerprint' -or
    $manifestSource -notmatch 'SHA256' -or $manifestSource -notmatch 'DomainId' -or $manifestSource -notmatch 'Impact' -or
    $generatorSource -notmatch 'ZemaxToolType' -or $generatorSource -notmatch 'ZemaxTool') {
  throw "Schemas, fingerprint, domain metadata, and impact metadata must converge in the shared static Host/Worker manifest assembly."
}
if ($hostProject -notmatch 'ZemaxMCP\.ToolManifest' -or $workerProject -notmatch 'ZemaxMCP\.ToolManifest') {
  throw "Both Host and Worker must consume the same generated tool manifest."
}
if ($hostSource -notmatch 'StaticToolManifest\.All' -or $hostSource -notmatch 'StaticToolManifest\.IsAllowed' -or
    $rpcClient -match 'ListToolsAsync|GetToolCatalog' -or $workerRpc -match 'GetToolCatalog') {
  throw "MCP discovery must be Host-owned; no private catalog RPC or compatibility shim may remain."
}
if ($workerRegistry -notmatch 'StaticToolManifest\.GetRequired' -or $workerRegistry -match 'BuildSchema\(|BuildTypeSchema\(') {
  throw "Worker execution must consume the shared manifest rather than maintain a second schema generator."
}
if ($rpcClient -notmatch 'PipeSecurity' -or $rpcClient -notmatch 'ZEMAX_MCP_PIPE_SECRET' -or
    $rpcClient -notmatch 'WorkerHandshake' -or $rpcClient -notmatch 'StaticToolManifest\.ContractFingerprint' -or
    $workerSource -notmatch 'WorkerHandshake' -or $workerSource -notmatch 'StaticToolManifest\.ContractFingerprint') {
  throw "The Host/Worker startup handshake must authenticate PID/secret and reject RPC or manifest contract mismatches before ZOS-API execution."
}
if ($workerRpc -notmatch 'ConcurrentQueue<ZemaxRpcEnvelope>' -or $workerRpc -notmatch 'PumpEventsAsync' -or
    $workerRpc -match '_ = WriteProgressAsync|_ = WriteSnapshotCreatedAsync' -or
    $rpcClient -notmatch 'Channel<ZemaxRpcEnvelope>' -or $rpcClient -notmatch 'DispatchEventsAsync' -or
    $rpcClient -notmatch '_progressHandlers' -or $hostSource -notmatch 'NotifyProgressAsync') {
  throw "Worker progress/snapshot events must use a serialized outbound queue and an independent Host dispatcher with MCP progress forwarding."
}
if ($hostSource -notmatch 'io\.zemaxmcp/clientInstanceId' -or $hostSource -notmatch 'X-Zemax-MCP-Client-Instance' -or
    $hostSource -notmatch 'IsSafeClientInstanceId' -or $originPolicy -notmatch 'X-Zemax-MCP-Client-Instance') {
  throw "Control identity must support a validated per-client instance identifier in addition to clientInfo and remote endpoint fallback."
}
if ($hostSource -notmatch 'OpticStudioControlLease' -or $hostSource -notmatch 'ResolveControlIdentity' -or
    $hostSource -match 'AllowAnyOrigin\(' -or $hostSource -notmatch 'zemax-mcp-remote-endpoint' -or
    $hostSource -match 'zemax-mcp-client-name|Mcp-Version' -or $hostSource -notmatch 'UseSetting\("AllowedHosts"' -or $hostOptions -notmatch 'allowed-origin') {
  throw "Control ownership and Host/Origin boundaries must remain explicit and non-wildcarded."
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
  throw "Tool execution must acquire the Host execution gate before checking cancelled-operation recovery."
}
if ($privateRpcTest -notmatch '2026-07-28' -or $privateRpcTest -notmatch 'io\.modelcontextprotocol/clientInfo' -or
    $privateRpcTest -notmatch 'io\.zemaxmcp/clientInstanceId' -or $privateRpcTest -notmatch 'bad-manifest' -or
    $privateRpcTest -notmatch 'VerifyProgressEventDispatchAsync' -or $privateRpcTest -notmatch 'same-name same-IP' -or
    $privateRpcTest -notmatch 'Send2026ListToolsAsync' -or $privateRpcTest -match '"initialize"') {
  throw "The E2E suite must cover stateless discovery, manifest mismatch, event dispatch, and distinct same-info client instances."
}
if ($schemaTest -notmatch 'StaticToolManifest\.All\.Count != 126' -or $schemaTest -notmatch 'zemax_open_file' -or
    $schemaTest -notmatch 'zemax_set_fields' -or $schemaTest -notmatch 'zemax_optimize' -or
    $schemaTest -notmatch 'unresolved opaque object contracts') {
  throw "Generated manifest regressions must verify count, policy metadata, required parameters, nested records, defaults, and absence of opaque contracts."
}

# Release-validation contract: modern stateless MCP is the primary live path;
# legacy initialize is an explicit compatibility probe rather than the default.
if ($liveVerifier -notmatch '2026-07-28' -or $liveVerifier -notmatch 'MCP-Protocol-Version' -or
    $liveVerifier -notmatch 'io\.modelcontextprotocol/clientInfo' -or $liveVerifier -notmatch 'io\.zemaxmcp/clientInstanceId' -or
    $liveVerifier -notmatch 'VerifyLegacyCompatibility' -or $liveVerifier -notmatch '2025-11-25' -or
    $liveVerifier -match '2024-11-05') {
  throw "The live release verifier must exercise 2026-07-28 stateless MCP by default and keep 2025-11-25 only as an explicit compatibility probe."
}
$listIndex = $liveVerifier.IndexOf('Invoke-ModernMcpRequest -Method "tools/list"', [StringComparison]::Ordinal)
$healthIndex = $liveVerifier.IndexOf('$health = Get-McpHealth', [StringComparison]::Ordinal)
if ($listIndex -lt 0 -or $healthIndex -lt 0 -or $listIndex -gt $healthIndex) {
  throw "Live release verification must exercise Host-only tools/list before the Worker-backed health endpoint."
}

# First reviewed sequential-editing regression: omitted values preserve state,
# while explicit false/empty values must be able to clear state in both directions.
if ($setSurfaceSource -notmatch 'material is not null' -or $setSurfaceSource -notmatch 'surface\.Material = material' -or
    $setSurfaceSource -notmatch 'comment is not null' -or $setSurfaceSource -notmatch 'surface\.Comment = comment' -or
    $setSurfaceSource -notmatch 'surface\.IsStop = isStop\.Value' -or $setSurfaceSource -notmatch 'ApplyVariableSolve' -or
    $setSurfaceSource -notmatch 'MakeSolveFixed\(\)' -or $setSurfaceSource -notmatch 'thicknessMin\.Value > thicknessMax\.Value' -or
    $setSurfaceSource -notmatch 'CancellationToken cancellationToken' -or $setSurfaceSource -notmatch '\}, cancellationToken\);') {
  throw "zemax_set_surface must preserve nullable omission semantics while supporting explicit clear/fixed operations and cancellation."
}

dotnet build (Join-Path $root "src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj") -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "Modern Host build failed." }
dotnet run --project (Join-Path $root "tests\ZemaxMCP.PrivateRpcTests\ZemaxMCP.PrivateRpcTests.csproj") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Private Host-to-Worker RPC integration verification failed." }
Write-Host "Modern .NET 10 Host / static manifest / private RPC v3 / release-verifier contract verification passed."
