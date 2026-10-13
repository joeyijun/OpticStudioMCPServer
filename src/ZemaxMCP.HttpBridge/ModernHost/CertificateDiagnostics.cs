using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ZemaxMCP.HttpBridge.ModernHost;

internal static class CertificateDiagnostics
{
    internal static object Read(X509Certificate2? certificate)
    {
        if (certificate == null) return new { enabled = false, status = "disabled" };
        var now = DateTimeOffset.UtcNow;
        var from = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var until = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        var days = (until - now).TotalDays;
        return new { enabled = true, fingerprintSha256 = certificate.GetCertHashString(HashAlgorithmName.SHA256),
            notBeforeUtc = from, notAfterUtc = until, daysRemaining = Math.Max(0, (int)Math.Ceiling(days)),
            renewalRecommended = days <= 30, status = now < from ? "not-yet-valid" : days <= 0 ? "expired" : days <= 30 ? "expires-soon" : "valid" };
    }
}
