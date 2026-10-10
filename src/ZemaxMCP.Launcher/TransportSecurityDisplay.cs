using System;

namespace ZemaxMCP.Launcher;

/// <summary>Reports the encryption of the CLIENT-to-endpoint connection,
/// not just the upstream Host's local Kestrel TLS flag. HTTPS terminators
/// and reverse proxies must never be mislabeled as plaintext HTTP.</summary>
internal static class TransportSecurityDisplay
{
    internal static string Explain(string endpoint,bool hostTlsEnabled)
    {
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri) ||
           (uri.Scheme!=Uri.UriSchemeHttp && uri.Scheme!=Uri.UriSchemeHttps))
            return "UNKNOWN — confirm URL scheme and certificate validation";
        if(uri.Scheme==Uri.UriSchemeHttps)
            return hostTlsEnabled ? "HTTPS/TLS" :
                "HTTPS/TLS to endpoint (Host reports no direct TLS; possible reverse-proxy termination)";
        if(uri.IsLoopback)
            return "local loopback HTTP (not encrypted)";
        return "UNENCRYPTED HTTP — bearer token and optical data exposed in transit";
    }
}
