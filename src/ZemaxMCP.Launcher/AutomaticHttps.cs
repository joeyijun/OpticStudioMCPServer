using System;
using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace ZemaxMCP.Launcher;

// No trust-store mutation occurs during generation or loading. Trust is a
// separate, explicitly confirmed UI action; public setup never contains a key.
internal static class AutomaticHttps
{
    internal sealed class Setup
    {
        internal string PfxPath { get; set; } = "";
        internal string Password { get; set; } = "";
        internal string PublicCertificate { get; set; } = "";
        internal string Fingerprint { get; set; } = "";
    }

    internal static Setup Create(string directory, string lanAddress)
    {
        directory = Path.GetFullPath(directory);
        if (!Path.IsPathRooted(directory)) throw new ArgumentException("Certificate directory must be absolute.");
        IPAddress? lan = null;
        if (!string.IsNullOrWhiteSpace(lanAddress) && !IPAddress.TryParse(lanAddress, out lan))
            throw new ArgumentException("HTTPS requires a valid selected LAN IP address.");
        Directory.CreateDirectory(directory);
        var sid = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current Windows user SID.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.SetOwner(sid);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        using var rsa = RSA.Create();
        rsa.KeySize = 3072;
        var request = new CertificateRequest("CN=Zemax MCP local HTTPS", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var usage = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usage, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        if (lan != null) names.AddIpAddress(lan);
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        var passwordBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(passwordBytes);
        var password = Convert.ToBase64String(passwordBytes);
        var path = Path.Combine(directory, "https-" + Guid.NewGuid().ToString("N") + ".pfx");
        // New unique filename: never overwrite a working certificate/key.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var pfx = cert.Export(X509ContentType.Pfx, password);
            file.Write(pfx, 0, pfx.Length);
        }
        return new Setup { PfxPath = path, Password = password,
            PublicCertificate = Convert.ToBase64String(cert.Export(X509ContentType.Cert)), Fingerprint = Fingerprint(cert) };
    }

    internal static string Fingerprint(X509Certificate2 certificate)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(certificate.RawData)).Replace("-", "");
    }

    internal static X509Certificate2 ReadPublic(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > 16384) throw new ArgumentException("Invalid public certificate.");
        var cert = new X509Certificate2(Convert.FromBase64String(base64));
        var constraints = cert.Extensions["2.5.29.19"] as X509BasicConstraintsExtension;
        var usage = cert.Extensions["2.5.29.37"] as X509EnhancedKeyUsageExtension;
        var serverOnly = usage != null && usage.EnhancedKeyUsages.Count == 1 && usage.EnhancedKeyUsages[0].Value == "1.3.6.1.5.5.7.3.1";
        if (cert.HasPrivateKey || constraints == null || constraints.CertificateAuthority || !serverOnly ||
            cert.NotBefore.ToUniversalTime() > DateTime.UtcNow || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
            cert.Extensions["2.5.29.17"] == null)
        { cert.Dispose(); throw new ArgumentException("Setup must contain a valid server-only leaf certificate, never a CA or private key."); }
        return cert;
    }

    internal static bool IsTrusted(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        foreach (var existing in store.Certificates)
            if (Fingerprint(existing) == Fingerprint(certificate)) return true;
        return false;
    }

    // Caller must show consent and a SHA-256 identity before invoking this.
    internal static void TrustCurrentUser(X509Certificate2 certificate)
    {
        using var checkedCertificate = ReadPublic(Convert.ToBase64String(certificate.RawData));
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(checkedCertificate);
    }
}
