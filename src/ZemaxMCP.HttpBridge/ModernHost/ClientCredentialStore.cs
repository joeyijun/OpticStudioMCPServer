using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Optional multi-client bearer authentication. The file stores SHA-256 token
/// digests, never raw bearer secrets, and is read on every protected request
/// so replacing/revoking a token takes effect without restarting the Host.
/// </summary>
internal sealed class ClientCredentialStore
{
    internal sealed record Credential(string Id, string Permission, byte[] Digest);

    private readonly string _filePath;
    internal ClientCredentialStore(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        Load(); // Invalid or missing configuration fails startup closed.
    }

    internal Credential? Authenticate(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var bearer = authorization.Substring("Bearer ".Length);
        if (string.IsNullOrWhiteSpace(bearer) || bearer != bearer.Trim() || bearer.Length > 4096)
            return null;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(bearer));
        Credential? matched = null;
        foreach (var client in Load())
        {
            if (CryptographicOperations.FixedTimeEquals(client.Digest, digest))
                matched = client;
        }
        return matched;
    }

    internal static bool IsReadOnly(ClaimsPrincipal? principal) =>
        string.Equals(principal?.FindFirst("zemax-mcp-permission")?.Value, "read-only", StringComparison.Ordinal);

    private IReadOnlyList<Credential> Load()
    {
        var file = new FileInfo(_filePath);
        if (!file.Exists || file.Length <= 0 || file.Length > 65536)
            throw new InvalidDataException("Client credential file is missing, empty, or exceeds 64 KiB.");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(_filePath));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1 ||
            !root.TryGetProperty("clients", out var clients) ||
            clients.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Credential file requires version=1 and a clients array.");

        foreach (var property in root.EnumerateObject())
            if (property.Name is not ("version" or "clients"))
                throw new InvalidDataException("Unexpected credential-file property: " + property.Name);

        if (clients.GetArrayLength() is < 1 or > 64)
            throw new InvalidDataException("Credential file must contain 1-64 clients.");

        var parsed = new List<Credential>(clients.GetArrayLength());
        var names = new HashSet<string>(StringComparer.Ordinal);
        var digests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in clients.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var idJson) || idJson.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("permission", out var permissionJson) || permissionJson.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("tokenSha256", out var digestJson) || digestJson.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Each credential requires id, permission, and tokenSha256 strings.");

            foreach (var property in item.EnumerateObject())
                if (property.Name is not ("id" or "permission" or "tokenSha256"))
                    throw new InvalidDataException("Unexpected client-credential property: " + property.Name);

            var id = idJson.GetString()!;
            var permission = permissionJson.GetString()!;
            var digestHex = digestJson.GetString()!;
            if (id.Length is < 1 or > 64 ||
                !id.All(ch => ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-') ||
                !names.Add(id))
                throw new InvalidDataException("Client IDs must be unique and contain 1-64 safe ASCII characters.");
            if (permission is not ("read-only" or "read-write"))
                throw new InvalidDataException("Client permission must be read-only or read-write.");
            if (digestHex.Length != 64 || !digestHex.All(Uri.IsHexDigit) || !digests.Add(digestHex))
                throw new InvalidDataException("Each client must use a distinct 64-digit SHA-256 bearer token digest.");

            parsed.Add(new Credential(id, permission, Convert.FromHexString(digestHex)));
        }
        return parsed;
    }
}
