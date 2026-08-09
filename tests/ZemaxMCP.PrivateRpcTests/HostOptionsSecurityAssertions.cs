using System.Runtime.CompilerServices;
using ZemaxMCP.HttpBridge.ModernHost;

namespace ZemaxMCP.PrivateRpcTests;

internal static class HostOptionsSecurityAssertions
{
    [ModuleInitializer]
    internal static void VerifyHostBindingAuthenticationBoundary()
    {
        var previousToken = Environment.GetEnvironmentVariable("ZEMAX_MCP_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_TOKEN", null);

            AssertAcceptedWithoutToken("127.0.0.1");
            AssertAcceptedWithoutToken("localhost");
            AssertAcceptedWithoutToken("[::1]");

            AssertRejectedWithoutToken("0.0.0.0");
            AssertRejectedWithoutToken("[::]");
            AssertRejectedWithoutToken("192.168.8.10");
            AssertRejectedWithoutToken("10.0.0.25");
            AssertRejectedWithoutToken("zemax-workstation.local");

            Environment.SetEnvironmentVariable("ZEMAX_MCP_TOKEN", "host-options-security-fixture");
            var lan = HostOptions.Parse(new[] { "--host", "192.168.8.10" });
            if (lan.Host != "192.168.8.10" || string.IsNullOrWhiteSpace(lan.AccessToken))
                throw new InvalidOperationException("An authenticated concrete LAN binding was unexpectedly rejected or lost its token.");

            var wildcard = HostOptions.Parse(new[]
            {
                "--host", "0.0.0.0",
                "--allowed-host", "192.168.8.10"
            });
            if (wildcard.Host != "0.0.0.0" || string.IsNullOrWhiteSpace(wildcard.AccessToken))
                throw new InvalidOperationException("An authenticated wildcard LAN binding with an explicit allowed host was unexpectedly rejected.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_TOKEN", previousToken);
        }
    }

    private static void AssertAcceptedWithoutToken(string host)
    {
        try
        {
            var options = HostOptions.Parse(new[] { "--host", host });
            if (!string.IsNullOrWhiteSpace(options.AccessToken))
                throw new InvalidOperationException($"Loopback host {host} unexpectedly acquired an access token.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Loopback host {host} must remain available without a token.", ex);
        }
    }

    private static void AssertRejectedWithoutToken(string host)
    {
        try
        {
            HostOptions.Parse(new[] { "--host", host });
        }
        catch (ArgumentException ex) when (ex.Message.Contains("ZEMAX_MCP_TOKEN", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException($"Non-loopback host {host} must be rejected when ZEMAX_MCP_TOKEN is not configured.");
    }
}
