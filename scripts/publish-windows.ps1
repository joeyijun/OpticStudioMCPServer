param(
  [string]$Configuration = "Release",
  [string]$ZemaxRoot = $env:ZEMAX_ROOT
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$verificationScript = Join-Path $PSScriptRoot "verify-tool-registration.ps1"
& $verificationScript -RepositoryRoot $root
$publish = Join-Path $root "artifacts\ZemaxMCP"

if ([string]::IsNullOrWhiteSpace($ZemaxRoot)) {
  throw "Set ZEMAX_ROOT to the installed OpticStudio folder before creating a release package."
}
foreach ($dll in "ZOSAPI.dll", "ZOSAPI_Interfaces.dll") {
  if (-not (Test-Path (Join-Path $ZemaxRoot $dll))) { throw "Missing $dll under ZEMAX_ROOT: $ZemaxRoot" }
}
$netHelperCandidates = @(
  (Join-Path $ZemaxRoot "ZOSAPI_NetHelper.dll"),
  (Join-Path $ZemaxRoot "ZOS-API\Libraries\ZOSAPI_NetHelper.dll"),
  (Join-Path $ZemaxRoot "ZOS_API\Libraries\ZOSAPI_NetHelper.dll")
)
$netHelperPath = $netHelperCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $netHelperPath) {
  throw "Missing ZOSAPI_NetHelper.dll under ZEMAX_ROOT or its ZOS-API\Libraries folder: $ZemaxRoot"
}
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item $publish -ItemType Directory -Force | Out-Null

dotnet build "$root\src\ZemaxMCP.Server\ZemaxMCP.Server.csproj" -c $Configuration -p:ZEMAX_ROOT="$ZemaxRoot" -p:ZOSAPI_NETHELPER_PATH="$netHelperPath"
dotnet publish "$root\src\ZemaxMCP.HttpBridge\ZemaxMCP.HttpBridge.csproj" -c $Configuration -r win-x64 --self-contained true -o "$root\artifacts\Host-publish"
dotnet build "$root\src\ZemaxMCP.ClientProxy\ZemaxMCP.ClientProxy.csproj" -c $Configuration
dotnet build "$root\src\ZemaxMCP.Launcher\ZemaxMCP.Launcher.csproj" -c $Configuration
dotnet build "$root\src\ZemaxMCP.Installer\ZemaxMCP.Installer.csproj" -c $Configuration
dotnet build "$root\src\ZemaxMCP.Updater\ZemaxMCP.Updater.csproj" -c $Configuration

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
  Copy-Item "$root\src\$project\bin\$Configuration\net48\*" $publish -Recurse -Force -Exclude "*.pdb", "*.xml", "*.log", "logs", "ZOSAPI*.dll", "ZemaxMCP.HttpBridge.exe", "ZemaxMCP.HttpBridge.exe.config", "ZemaxMCP.Server.exe", "ZemaxMCP.Server.exe.config"
}
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
Compress-Archive "$publish\*" "$root\artifacts\ZemaxMCP-win-x64.zip" -Force
Write-Host "Release package: $root\artifacts\ZemaxMCP-win-x64.zip"
