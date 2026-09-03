param(
  [string]$Configuration = "Release",
  [string]$ZemaxRoot = $env:ZEMAX_ROOT,
  [string]$ZosApiBuildRoot = $env:ZEMAX_API_BASELINE_ROOT
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$verificationScript = Join-Path $PSScriptRoot "verify-tool-registration.ps1"
& $verificationScript -RepositoryRoot $root
$publish = Join-Path $root "artifacts\ZemaxMCP"

# Release compatibility is determined by the ZOS-API assemblies used to compile
# the net48 Worker. Prefer an explicitly configured oldest-supported API
# baseline; keep -ZemaxRoot / ZEMAX_ROOT as a backwards-compatible fallback.
if ([string]::IsNullOrWhiteSpace($ZosApiBuildRoot)) {
  $ZosApiBuildRoot = $ZemaxRoot
  if (-not [string]::IsNullOrWhiteSpace($ZosApiBuildRoot)) {
    Write-Warning "ZEMAX_API_BASELINE_ROOT was not set. This package will use '$ZosApiBuildRoot' as its minimum ZOS-API build baseline; older OpticStudio installations will be rejected at Worker startup."
  }
}
if ([string]::IsNullOrWhiteSpace($ZosApiBuildRoot)) {
  throw "Set ZEMAX_API_BASELINE_ROOT to the oldest OpticStudio installation supported by this release (recommended), or set ZEMAX_ROOT for a single-version/developer package."
}
$ZosApiBuildRoot = (Resolve-Path -LiteralPath $ZosApiBuildRoot).Path

$opticStudioPath = Join-Path $ZosApiBuildRoot "OpticStudio.exe"
$zosApiPath = Join-Path $ZosApiBuildRoot "ZOSAPI.dll"
$interfacesPath = Join-Path $ZosApiBuildRoot "ZOSAPI_Interfaces.dll"
foreach ($file in $opticStudioPath, $zosApiPath, $interfacesPath) {
  if (-not (Test-Path -LiteralPath $file)) { throw "Missing OpticStudio/ZOS-API build-baseline component: $file" }
}
$netHelperCandidates = @(
  (Join-Path $ZosApiBuildRoot "ZOSAPI_NetHelper.dll"),
  (Join-Path $ZosApiBuildRoot "ZOS-API\Libraries\ZOSAPI_NetHelper.dll"),
  (Join-Path $ZosApiBuildRoot "ZOS_API\Libraries\ZOSAPI_NetHelper.dll")
)
$netHelperPath = $netHelperCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $netHelperPath) {
  throw "Missing ZOSAPI_NetHelper.dll under ZOS-API build baseline root or its ZOS-API\Libraries folder: $ZosApiBuildRoot"
}

function Get-VersionMetadata([string]$Path) {
  $file = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
  $fileVersion = if ($null -eq $file.FileVersion) { "" } else { $file.FileVersion }
  $productVersion = if ($null -eq $file.ProductVersion) { "" } else { $file.ProductVersion }
  $assemblyVersion = ""
  try { $assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($Path).Version.ToString() }
  catch [System.BadImageFormatException] { }
  return [PSCustomObject]@{
    FileVersion = $fileVersion.Replace("`r", " ").Replace("`n", " ").Trim()
    ProductVersion = $productVersion.Replace("`r", " ").Replace("`n", " ").Trim()
    AssemblyVersion = $assemblyVersion
  }
}

function Write-ZosApiBuildInfo([string]$Destination) {
  $components = [ordered]@{
    "OpticStudio" = $opticStudioPath
    "ZOSAPI_Interfaces" = $interfacesPath
    "ZOSAPI" = $zosApiPath
    "ZOSAPI_NetHelper" = $netHelperPath
  }
  $lines = New-Object System.Collections.Generic.List[string]
  $lines.Add("format=1")
  $lines.Add("policy=runtime-must-not-be-older-than-build")
  foreach ($entry in $components.GetEnumerator()) {
    $metadata = Get-VersionMetadata $entry.Value
    $lines.Add("$($entry.Key).fileVersion=$($metadata.FileVersion)")
    $lines.Add("$($entry.Key).productVersion=$($metadata.ProductVersion)")
    $lines.Add("$($entry.Key).assemblyVersion=$($metadata.AssemblyVersion)")
  }
  Set-Content -LiteralPath $Destination -Value $lines -Encoding UTF8
}

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item $publish -ItemType Directory -Force | Out-Null

Write-Host "Building Worker against ZOS-API baseline: $ZosApiBuildRoot"
dotnet build "$root\src\ZemaxMCP.Server\ZemaxMCP.Server.csproj" -c $Configuration -p:ZEMAX_ROOT="$ZosApiBuildRoot" -p:ZOSAPI_NETHELPER_PATH="$netHelperPath"
if ($LASTEXITCODE -ne 0) { throw "Worker build failed." }
dotnet publish "$root\src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj" -c $Configuration -r win-x64 --self-contained true -o "$root\artifacts\Host-publish"
if ($LASTEXITCODE -ne 0) { throw "Host publish failed." }
dotnet build "$root\src\ZemaxMCP.ClientProxy\ZemaxMCP.ClientProxy.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Client proxy build failed." }
dotnet build "$root\src\ZemaxMCP.Launcher\ZemaxMCP.Launcher.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Launcher build failed." }
dotnet build "$root\src\ZemaxMCP.Installer\ZemaxMCP.Installer.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
dotnet build "$root\src\ZemaxMCP.Updater\ZemaxMCP.Updater.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Updater build failed." }

$projects = "ZemaxMCP.Server", "ZemaxMCP.ClientProxy", "ZemaxMCP.Launcher", "ZemaxMCP.Installer", "ZemaxMCP.Updater"
$releaseAssemblies = @{}
foreach ($project in $projects) {
  Get-ChildItem "$root\src\$project\bin\$Configuration\net48" -Filter "*.dll" -File | Where-Object {
    $_.Name -notlike "ZOSAPI*.dll"
  } | ForEach-Object {
    try {
      $assembly = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName)
      $identity = "$($assembly.Name), Version=$($assembly.Version), PublicKeyToken=$([BitConverter]::ToString($assembly.GetPublicKeyToken()).Replace('-', '').ToLowerInvariant())"
      if ($releaseAssemblies.ContainsKey($assembly.Name) -and $releaseAssemblies[$assembly.Name].Identity -ne $identity) {
        throw "Release package assembly conflict for $($assembly.Name): $($releaseAssemblies[$assembly.Name].Identity) from $($releaseAssemblies[$assembly.Name].Project), but $identity from $project. Align package versions before publishing."
      }
      $releaseAssemblies[$assembly.Name] = [PSCustomObject]@{ Identity = $identity; Project = $project }
    }
    catch [System.BadImageFormatException] {
      # Native DLLs are not CLR assemblies and do not participate in .NET binding.
    }
  }
}
New-Item (Join-Path $publish "Host") -ItemType Directory -Force | Out-Null
Copy-Item "$root\artifacts\Host-publish\*" (Join-Path $publish "Host") -Recurse -Force -Exclude "*.pdb", "*.xml", "*.log", "logs"
foreach ($project in $projects) {
  # PDB files contain the absolute source path used by the release builder.
  # They are not needed to run the application and would expose that path in
  # user-facing exception logs.
  Copy-Item "$root\src\$project\bin\$Configuration\net48\*" $publish -Recurse -Force -Exclude "*.pdb", "*.xml", "*.log", "logs", "ZOSAPI*.dll", "ZemaxMCP.HttpBridge.exe", "ZemaxMCP.Server.exe.config", "ZemaxMCP.Server.exe", "ZemaxMCP.HttpBridge.exe.config"
}

# Record only version identities, never the release builder's installation path.
# OpticStudio.exe ProductVersion is the primary product-release anchor; the
# three ZOS-API DLL versions are retained as secondary cross-checks.
Write-ZosApiBuildInfo (Join-Path $publish "ZOSAPI_BUILD_INFO.txt")

$launcherAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe")).Version
if ($null -eq $launcherAssemblyVersion) { throw "Could not determine the launcher version for the release package." }
$launcherVersion = $launcherAssemblyVersion.ToString(3)
Set-Content -LiteralPath (Join-Path $publish "VERSION.txt") -Value $launcherVersion -NoNewline
Copy-Item "$root\installer\Portable-Install.cmd" "$publish\Portable-Install.cmd" -Force
Copy-Item "$root\installer\Start-Zemax-MCP.cmd" "$publish\Start-Zemax-MCP.cmd" -Force
Copy-Item "$root\LICENSE" "$publish\LICENSE" -Force
Copy-Item "$root\THIRD_PARTY_NOTICES.md" "$publish\THIRD_PARTY_NOTICES.md" -Force
$forbiddenReleaseFiles = @(Get-ChildItem $publish -Recurse -File | Where-Object {
  $_.Extension -in ".pdb", ".log" -or $_.Name -like "ZOSAPI*.dll" -or $_.Name -in "ZemaxMCP.HttpBridge.exe", "ZemaxMCP.Server.exe"
})
if ($forbiddenReleaseFiles.Count -gt 0) {
  throw "Release staging contains forbidden files: $($forbiddenReleaseFiles.FullName -join ', ')"
}
foreach ($requiredExecutable in "ZemaxMCP.Worker.exe", "Start-Zemax-MCP.exe") {
  if (-not (Test-Path -LiteralPath (Join-Path $publish $requiredExecutable))) { throw "Release staging is missing $requiredExecutable." }
}
if (-not (Test-Path -LiteralPath (Join-Path $publish "Host\ZemaxMCP.Host.exe"))) { throw "Release staging is missing Host\ZemaxMCP.Host.exe." }
if (-not (Test-Path -LiteralPath (Join-Path $publish "ZOSAPI_BUILD_INFO.txt"))) { throw "Release staging is missing the ZOS-API compatibility baseline marker." }
Compress-Archive "$publish\*" "$root\artifacts\ZemaxMCP-win-x64.zip" -Force
Write-Host "Release package: $root\artifacts\ZemaxMCP-win-x64.zip"
