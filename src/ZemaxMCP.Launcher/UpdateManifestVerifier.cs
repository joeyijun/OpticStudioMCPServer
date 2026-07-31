using System;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace ZemaxMCP.Launcher;

internal static class UpdateManifestVerifier
{
    // Public half of the v1.1 release signing key. The private half is held only by the release maintainer/GitHub secret.
    private const string PublicKeyBase64 = "BgIAAACkAABSU0ExAAwAAAEAAQBN/K41HPNoMsxjlyveSzKjeZeVwvEQjIr8M/yWo0LoUJQJDs2/XP7YqCpes86FTPtzO24ic285di+XJRbUmnWch90/X4uVfAe2Pg++DHBnUzqAfv5zyjhpgFH4h4b30EvqG7rCaGKRNpJlLnxvG1CIonop3ZCSjNgNekjR1PWmsPcGlPiY6WS++w3GuPoZdS1E0hfjhCCcFCJ+FIvm8ocav2SXsRcIR6DWSJeErV6m1fjk7aVtttTG4IqDT/6eaF+0tkQ/sf5+XmKyRGFdNqfV1s/84sbLvEJmoXjHCZLy9sZ9A1h270FHyLjB9ovXOB7EBBKhyN+WTfto8gAO8wiftMaZKdSCzeXJVgpmP+qbCxKowXBC/aEPG4OIBMF/WSvh5EpWGYXqap1cVP6AO5btt6yp3Ba3e3trQJeZmiNAIoUOkYOfEm7DUMda5Tzu+Dsn8LMJxj+9nJ6zOBsiJ7Dt6At7v51zkPMfAVuQ6A+VD2pdM2A1reO9qyc2+W0jf90=";

    public static UpdatePayload Verify(string manifestJson, string expectedVersion, string expectedAssetName, string packagePath)
    {
        var manifest = JObject.Parse(manifestJson);
        if (!string.Equals(manifest["algorithm"]?.ToString(), "RS256", StringComparison.Ordinal))
            throw new InvalidDataException("The update manifest uses an unsupported signature algorithm.");
        var payloadBytes = Convert.FromBase64String(manifest["payload"]?.ToString() ?? throw new InvalidDataException("The update manifest has no payload."));
        var signature = Convert.FromBase64String(manifest["signature"]?.ToString() ?? throw new InvalidDataException("The update manifest has no signature."));
        using (var rsa = new RSACryptoServiceProvider())
        {
            rsa.ImportCspBlob(Convert.FromBase64String(PublicKeyBase64));
            if (!rsa.VerifyData(payloadBytes, CryptoConfig.MapNameToOID("SHA256"), signature))
                throw new CryptographicException("The update manifest signature is invalid.");
        }

        var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);
        var payload = JObject.Parse(payloadJson);
        var result = new UpdatePayload(
            payload["schemaVersion"]?.Value<int>() ?? 0,
            payload["version"]?.ToString() ?? "",
            payload["assetName"]?.ToString() ?? "",
            payload["sha256"]?.ToString() ?? "",
            payload["size"]?.Value<long>() ?? -1);
        if (result.SchemaVersion != 1) throw new InvalidDataException("The update manifest schema is unsupported.");
        if (!string.Equals(result.Version, expectedVersion, StringComparison.Ordinal)) throw new InvalidDataException("The manifest version does not match the GitHub release tag.");
        if (!string.Equals(result.AssetName, expectedAssetName, StringComparison.Ordinal)) throw new InvalidDataException("The manifest asset does not match the downloaded package.");
        var file = new FileInfo(packagePath);
        if (!file.Exists || file.Length != result.Size) throw new InvalidDataException("The downloaded update size does not match the signed manifest.");
        using (var stream = file.OpenRead())
        using (var sha = SHA256.Create())
        {
            var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (!string.Equals(actual, result.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("The downloaded update SHA256 does not match the signed manifest.");
        }
        return result;
    }
}

internal sealed class UpdatePayload
{
    public UpdatePayload(int schemaVersion, string version, string assetName, string sha256, long size)
    { SchemaVersion = schemaVersion; Version = version; AssetName = assetName; Sha256 = sha256; Size = size; }
    public int SchemaVersion { get; }
    public string Version { get; }
    public string AssetName { get; }
    public string Sha256 { get; }
    public long Size { get; }
}
