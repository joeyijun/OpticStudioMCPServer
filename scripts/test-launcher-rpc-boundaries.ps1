param([string]$Configuration = 'Release')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"))
Add-Type -Path (Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Newtonsoft.Json.dll")
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$parse=$assembly.GetType('ZemaxMCP.Launcher.JobActionClient').GetMethod('ParseJobResponse',$flags)
$arguments=New-Object object[] 4
$arguments[1]=[bool]$true; $arguments[2]='job-a'; $arguments[3]=[Newtonsoft.Json.Linq.JValue]::new(1)
$prefix=': heartbeat'+"`n`n"+'data: {"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":2}}'+"`n`n"+
 'data: {"jsonrpc":"2.0","id":1,"method":"sampling/createMessage","params":{}}'+"`n`n"+
 'data: {"jsonrpc":"2.0","id":9,"result":{"ignored":true}}'+"`n`n"
$result=[Newtonsoft.Json.Linq.JObject]::Parse('{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"{\"jobId\":\"job-a\",\"state\":\"Cancelled\"}"}]}}')
$json=$result.ToString([Newtonsoft.Json.Formatting]::None)
$split=$json.IndexOf('"result"')
$arguments[0]=$prefix+'data: '+$json.Substring(0,$split)+"`n"+'data: '+$json.Substring($split)+"`n`n"
$parsed=$parse.Invoke($null,$arguments) | ConvertFrom-Json
if($parsed.jobId -ne 'job-a' -or $parsed.state -ne 'Cancelled') {throw 'SSE notifications/server requests hid the correct Job response.'}
$arguments[1]=[bool]$false
foreach($bad in @(
 '{"jsonrpc":"2.0","id":9,"result":{}}',
 '{"jsonrpc":"2.0","id":1,"result":{"content":[{"text":"null"}]}}',
 '{"jsonrpc":"2.0","id":1,"result":{"content":[{"text":"{\"jobId\":\"foreign-job\"}"}]}}',
 '{"jsonrpc":"2.0","id":1,"result":{"isError":true,"content":[{"text":"{\"code\":\"job_not_owned\",\"message\":\"Denied\"}"}]}}'
)) {
 $arguments[0]=[string]$bad; $rejected=$false
 try {[void]$parse.Invoke($null,$arguments)} catch {$rejected=$true}
 if(!$rejected) {throw 'Malformed, absent or foreign Job result accepted.'}
}
$type=$assembly.GetType('ZemaxMCP.Launcher.ScopedDeltaCursor')
$cursor=[Activator]::CreateInstance($type,$true)
$instanceFlags=[Reflection.BindingFlags]'Instance,NonPublic'
$bind=$type.GetMethod('Bind',$instanceFlags); $matches=$type.GetMethod('Matches',$instanceFlags)
$scope=New-Object object[] 2; $scope[0]='https://example.com/MCP'; $scope[1]='token-a'
[void]$bind.Invoke($cursor,$scope)
$scope[0]='HTTPS://EXAMPLE.COM/MCP'
if(!$matches.Invoke($cursor,$scope)) {throw 'Equivalent scheme/host was not normalized.'}
$scope[0]='https://example.com/mcp'
if($matches.Invoke($cursor,$scope)) {throw 'Case-sensitive endpoint paths shared a diagnostic cursor.'}
$scope[0]='https://example.com/MCP'; $scope[1]='token-b'
if($matches.Invoke($cursor,$scope)) {throw 'Different credentials shared a diagnostic cursor.'}
Write-Output 'Launcher RPC boundary passed: multiline SSE, notification/server-request filtering, request/Job IDs, errors and case-sensitive credential-bound endpoints.'
