using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace ZemaxMCP.PrivateRpcTests;

internal static partial class Program
{
    private static async Task VerifyHttpsEndToEndAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ZemaxMCP-https-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Zemax MCP HTTPS test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = Path.Combine(directory, "test.pfx");
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx, password));
        var port = ReserveLoopbackPort();
        var host = Path.Combine(FindRepositoryRoot(), "src", "ZemaxMCP.HttpBridge", "bin", "Release", "net10.0-windows", "ZemaxMCP.Host.exe");
        var start = new ProcessStartInfo(host, $"--worker \"{Environment.ProcessPath}\" --host 127.0.0.1 --port {port} --log-dir \"{directory}\" --tls-pfx \"{pfx}\"")
        { UseShellExecute = false, CreateNoWindow = true };
        start.Environment["ZEMAX_MCP_TLS_PFX_PASSWORD"] = password;
        start.Environment["ZEMAX_MCP_TOKEN"] = "https-fixture-token";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("HTTPS Host did not start.");
        try
        {
            // Test-only private trust root. No system trust-store changes and no
            // accept-all callback: hostname, exact leaf and chain remain checked.
            using var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (_, leaf, _, errors) =>
            {
                if (leaf == null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0 ||
                    !leaf.RawData.AsSpan().SequenceEqual(certificate.RawData)) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(leaf);
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.Authorization = new("Bearer", "https-fixture-token");
            var endpoint = $"https://127.0.0.1:{port}/mcp";
            Exception? failure = null;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try
                {
                    using var health = await client.GetAsync(endpoint + "/health");
                    health.EnsureSuccessStatusCode();
                    var body = await health.Content.ReadAsStringAsync();
                    if (!body.Contains("\"tlsEnabled\":true") || !body.Contains("fake-license")) throw new Exception("HTTPS health contract failed: " + body);
                    using var details = System.Text.Json.JsonDocument.Parse(body);
                    var certInfo = details.RootElement.GetProperty("tlsCertificate");
                    if (certInfo.GetProperty("fingerprintSha256").GetString() != certificate.GetCertHashString(HashAlgorithmName.SHA256) ||
                        certInfo.GetProperty("status").GetString() != "expires-soon" ||
                        !certInfo.GetProperty("renewalRecommended").GetBoolean() ||
                        !details.RootElement.GetProperty("build").GetProperty("informationalVersion").GetString()!.Contains('+'))
                        throw new Exception("HTTPS expiry/build diagnostics failed.");
                    failure = null;
                    break;
                }
                catch (Exception ex) { failure = ex; }
                if (process.HasExited) break;
                await Task.Delay(100);
            }
            if (failure != null) throw new InvalidOperationException("Real Windows HTTPS handshake/health failed.", failure);
            using (var denied = new HttpRequestMessage(HttpMethod.Get, endpoint + "/health"))
            {
                denied.Headers.Authorization = new("Bearer", "wrong-token");
                using var result = await client.SendAsync(denied);
                if (result.StatusCode != HttpStatusCode.Unauthorized) throw new Exception("HTTPS bypassed token validation.");
            }
            using (var untrusted = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            {
                var rejected = false;
                try { using var response = await untrusted.GetAsync(endpoint + "/health"); }
                catch (HttpRequestException) { rejected = true; }
                if (!rejected) throw new Exception("Untrusted HTTPS certificate was accepted by default validation.");
            }
            foreach (var method in new[] { "tools/list", "tools/call", "server/discover" })
            {
                var parameters = method == "tools/call" ? "\"name\":\"zemax_status\",\"arguments\":{}," : "";
                var body = "{\"jsonrpc\":\"2.0\",\"id\":17,\"method\":\"" + method + "\",\"params\":{" + parameters +
                    "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientInfo\":{\"name\":\"https-test\",\"version\":\"1\"},\"io.modelcontextprotocol/clientCapabilities\":{}}}}";
                using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                message.Headers.Add("MCP-Protocol-Version", "2026-07-28");
                message.Headers.Add("Mcp-Method", method);
                if (method == "tools/call") message.Headers.Add("Mcp-Name", "zemax_status");
                message.Headers.Add("Accept", "application/json, text/event-stream");
                message.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var result = await client.SendAsync(message);
                result.EnsureSuccessStatusCode();
                var response = await result.Content.ReadAsStringAsync();
                if (!response.Contains("\"result\"") || response.Contains("\"isError\":true")) throw new Exception(method + " over HTTPS failed: " + response);
            }
            Console.WriteLine("HTTPS E2E passed: real Windows TLS, authenticated health, tools/list, tools/call and discovery; no global certificate bypass or trust-store writes.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            // Child process handles may outlive the Host by a few milliseconds.
            File.Delete(pfx);
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(directory, recursive: true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(100); }
            }
        }
    }
}
