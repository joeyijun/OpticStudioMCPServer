param([string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $root "src\ZemaxMCP.Launcher\bin\$Configuration\net48\Start-Zemax-MCP.exe"))
$type=$assembly.GetType('ZemaxMCP.Launcher.AutomaticHttps')
$flags=[Reflection.BindingFlags]'Static,NonPublic'
$instance=[Reflection.BindingFlags]'Instance,NonPublic,Public'
$directory=Join-Path ([IO.Path]::GetTempPath()) ('ZemaxMCP-cert-test-'+[Guid]::NewGuid().ToString('N'))
try {
    $createArgs=New-Object object[] 2
    $createArgs[0]=[string]$directory
    $createArgs[1]=[string]'192.168.8.1'
    $setup=$type.GetMethod('Create',$flags).Invoke($null,$createArgs)
    function Property($value,$name) { $value.GetType().GetProperty($name,$instance).GetValue($value,$null) }
    $path=Property $setup 'PfxPath'
    $password=Property $setup 'Password'
    $public=Property $setup 'PublicCertificate'
    $fingerprint=Property $setup 'Fingerprint'
    $readArgs=New-Object object[] 1
    $readArgs[0]=[string]$public
    $certificate=$type.GetMethod('ReadPublic',$flags).Invoke($null,$readArgs)
    try {
        if ($certificate.HasPrivateKey) { throw 'Public setup exposes a private key.' }
        if ($type.GetMethod('Fingerprint',$flags).Invoke($null,@($certificate)) -ne $fingerprint) { throw 'Public fingerprint mismatch.' }
        if ($type.GetMethod('IsTrusted',$flags).Invoke($null,@($certificate))) { throw 'Generation silently modified the trust store.' }
        $san=[BitConverter]::ToString($certificate.Extensions['2.5.29.17'].RawData).Replace('-','')
        if ($san -notmatch '8704C0A80801' -or $san -notmatch '87047F000001') { throw 'Selected LAN or loopback IP SAN missing.' }
        $pfx=[Security.Cryptography.X509Certificates.X509Certificate2]::new($path,$password,[Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        try { if (-not $pfx.HasPrivateKey) { throw 'Host PFX has no private key.' } } finally { $pfx.Dispose() }
        $acl=Get-Acl -LiteralPath $directory
        if (-not $acl.AreAccessRulesProtected) { throw 'Certificate-directory inheritance is not restricted.' }
        $allowed=@([Security.Principal.WindowsIdentity]::GetCurrent().User.Value,'S-1-5-18')
        foreach ($rule in $acl.Access) {
            $sid=$rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
            if ($rule.AccessControlType -eq 'Allow' -and $sid -notin $allowed) { throw 'Unexpected principal can read private certificates.' }
        }
        # A CA must never be imported by the clipboard trust flow.
        $rsa=[Security.Cryptography.RSA]::Create()
        try {
            $request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Unwanted CA',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true,$false,0,$true))
            $ca=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddDays(1))
            try {
                $accepted=$false
                try { $type.GetMethod('ReadPublic',$flags).Invoke($null,@([Convert]::ToBase64String($ca.RawData))) | Out-Null; $accepted=$true } catch { }
                if ($accepted) { throw 'Clipboard flow accepted a CA certificate.' }
            } finally { $ca.Dispose() }
        } finally { $rsa.Dispose() }
    } finally { $certificate.Dispose() }
    Write-Host 'Automatic HTTPS: generation, leaf-only public sharing, SANs, private PFX, restrictive ACL, no implicit trust, and CA rejection passed.'
} finally {
    if (Test-Path -LiteralPath $directory) {
        $resolved=(Resolve-Path -LiteralPath $directory).Path
        if (-not $resolved.StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'ZemaxMCP-cert-test-*') { throw 'Unsafe certificate-test cleanup.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
