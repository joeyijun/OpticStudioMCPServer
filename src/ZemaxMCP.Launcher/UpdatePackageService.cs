using System;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace ZemaxMCP.Launcher;

// Package verification/extraction is independent of window controls and service
// lifetime. The window retains responsibility for maintenance admission/handoff.
internal static class UpdatePackageService
{
    internal static Version ParseReleaseVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var suffix = value.IndexOf('-');
        if (suffix >= 0) value = value.Substring(0, suffix);
        if (!Version.TryParse(value, out var parsed))
            throw new System.IO.InvalidDataException("The release tag is not a supported version: " + tag);
        return NormalizeVersion(parsed);
    }

    internal static Version NormalizeVersion(Version version) =>
        new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    internal static Task VerifyAndExtractAsync(string manifest, string tag,
        string zip, string staging, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateManifestVerifier.Verify(manifest, tag, "ZemaxMCP-win-x64.zip", zip);
        cancellationToken.ThrowIfCancellationRequested();
        ZipFile.ExtractToDirectory(zip, staging);
        cancellationToken.ThrowIfCancellationRequested();
    }, cancellationToken);
}
