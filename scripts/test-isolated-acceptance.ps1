$ErrorActionPreference='Stop'
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-isolation-fixture-'+[Guid]::NewGuid().ToString('N'))
try {
    foreach ($relative in @('package\Host\ZemaxMCP.Host.exe','package\ZemaxMCP.Worker.exe','package\VERSION.txt','zemax\ZOSAPI.dll',
        'samples\Sequential\Objectives\Cooke 40 degree field.zmx',
        'samples\Non-sequential\Sources\Diode sample.ZMX',
        'samples\Short course\Optical System Design Using OpticStudio\SC_Tol_Cooke.zmx',
        'samples\Short course\Optical System Design Using OpticStudio\Advanced_SC_doubleGauss_final.ZMX')) {
        $path=Join-Path $fixtureRoot $relative
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
        [IO.File]::WriteAllText($path,'test-fixture-only')
    }
    $parameters=@{PackageRoot=(Join-Path $fixtureRoot 'package');ZemaxRoot=(Join-Path $fixtureRoot 'zemax');SamplesRoot=(Join-Path $fixtureRoot 'samples')}
    $plan=@(& (Join-Path $PSScriptRoot 'Run-IsolatedAcceptance.ps1') @parameters -PlanOnly)
    if ($plan.Count -ne 4 -or @($plan | Where-Object { -not $_.Name -or -not $_.Path }).Count) { throw 'Isolated fixture plan regressed.' }
    $rejected=$false
    try { & (Join-Path $PSScriptRoot 'Run-IsolatedAcceptance.ps1') @parameters | Out-Null }
    catch { if ($_.Exception.Message -match 'AllowWorkerTermination') {$rejected=$true} else {throw} }
    if (-not $rejected) { throw 'Isolated destructive workflow ran without explicit permission.' }
    $rejected=$false
    try {
        & (Join-Path $PSScriptRoot 'verify-live-functional.ps1') -FixturePath $plan[3].Path -AllowReplaceCurrentSystem `
            -VerifyBackgroundJobs -VerifyOfficialTasks -VerifyWorkerCrashRecovery -AllowWorkerTermination | Out-Null
    } catch { if ($_.Exception.Message -match 'independently launched test Host PID') {$rejected=$true} else {throw} }
    if (-not $rejected) { throw 'Destructive verifier accepted missing process ownership.' }
    foreach ($file in @('Run-IsolatedAcceptance.ps1','verify-live-functional.ps1','verify-live-mcp.ps1','McpHttpResponse.ps1')) {
        $syntaxTokens=$null; $syntaxErrors=$null
        [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $file),[ref]$syntaxTokens,[ref]$syntaxErrors)
        if ($syntaxErrors.Count) { throw "Syntax error: $file" }
    }
} finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        $resolved=(Resolve-Path -LiteralPath $fixtureRoot).Path
        if (-not $resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notlike 'ZemaxMCP-isolation-fixture-*') { throw 'Unsafe fixture cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Output 'Isolated acceptance planning, permission/process-ownership rejection and PowerShell syntax passed; no processes started.'
