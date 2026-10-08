using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZemaxMCP.HttpBridge.ModernHost;

namespace ZemaxMCP.PrivateRpcTests;

internal static class ScopedCredentialAssertions
{
    internal static void Verify()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZemaxMCP-credentials-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "credentials.json");
        const string readerToken = "test-only-reader-" + "42aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string writerToken = "test-only-writer-" + "57bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        try
        {
            Write(path, ("reader", "read-only", readerToken), ("writer", "read-write", writerToken));
            var store = new ClientCredentialStore(path);
            if (store.Authenticate("Bearer " + readerToken) is not { Id: "reader", Permission: "read-only" } ||
                store.Authenticate("Bearer " + writerToken) is not { Id: "writer", Permission: "read-write" } ||
                store.Authenticate("Bearer bogus") != null ||
                store.Authenticate("Basic " + readerToken) != null)
                throw new InvalidOperationException("Scoped credentials did not authenticate distinct bearer principals and reject invalid secrets.");

            // Revocation and role changes are effective on the next request.
            Write(path, ("reader", "read-write", readerToken));
            if (store.Authenticate("Bearer " + writerToken) != null ||
                store.Authenticate("Bearer " + readerToken)?.Permission != "read-write")
                throw new InvalidOperationException("Client credential changes or revocation required a Host restart.");

            var oldSharedToken = Environment.GetEnvironmentVariable("ZEMAX_MCP_TOKEN");
            var oldClientsFile = Environment.GetEnvironmentVariable("ZEMAX_MCP_CLIENTS_FILE");
            try
            {
                Environment.SetEnvironmentVariable("ZEMAX_MCP_TOKEN", null);
                Environment.SetEnvironmentVariable("ZEMAX_MCP_CLIENTS_FILE", null);
                var lan = HostOptions.Parse(new[] { "--host", "192.168.8.10", "--client-credentials-file", path });
                if (lan.ClientCredentialsFile != Path.GetFullPath(path))
                    throw new InvalidOperationException("LAN binding did not admit scoped authentication without the shared token.");

                Environment.SetEnvironmentVariable("ZEMAX_MCP_TOKEN", "old-shared-token");
                try
                {
                    HostOptions.Parse(new[] { "--client-credentials-file", path });
                    throw new InvalidOperationException("Shared and scoped credential modes were incorrectly combined.");
                }
                catch (ArgumentException ex) when (ex.Message.Contains("mutually exclusive", StringComparison.Ordinal)) { }
            }
            finally
            {
                Environment.SetEnvironmentVariable("ZEMAX_MCP_TOKEN", oldSharedToken);
                Environment.SetEnvironmentVariable("ZEMAX_MCP_CLIENTS_FILE", oldClientsFile);
            }

            File.WriteAllText(path, "{ \"version\": 1, \"clients\": [] }");
            AssertInvalid(() => new ClientCredentialStore(path), "empty credential list");
            File.WriteAllText(path, "{ invalid json");
            AssertInvalid(() => store.Authenticate("Bearer " + readerToken), "malformed credentials must fail closed");
            File.Delete(path);
            AssertInvalid(() => store.Authenticate("Bearer " + readerToken), "deleted credentials must fail closed");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    internal static void Write(string path, params (string Id, string Permission, string Token)[] clients)
    {
        var payload = new
        {
            version = 1,
            clients = clients.Select(client => new
            {
                id = client.Id,
                permission = client.Permission,
                tokenSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.Token))).ToLowerInvariant()
            }).ToArray()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
    }

    private static void AssertInvalid(Action action, string scenario)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException) { return; }
        throw new InvalidOperationException("Scoped credential configuration was incorrectly accepted: " + scenario);
    }
}
