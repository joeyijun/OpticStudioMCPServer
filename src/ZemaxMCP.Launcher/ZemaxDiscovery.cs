using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZemaxMCP.Launcher;

/// <summary>
/// Describes one usable OpticStudio program directory. License files are not
/// copied or parsed: their presence is diagnostic evidence only. Runtime
/// validity is determined by ZOS-API after the application is created.
/// </summary>
public sealed class ZemaxInstallation
{
    public string Root { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string DiscoverySource { get; set; } = "";
    public string ZosApiPath { get; set; } = "";
    public string ZosApiInterfacesPath { get; set; } = "";
    public string NetHelperPath { get; set; } = "";
    public string OpticStudioExecutablePath { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string DataDirectorySource { get; set; } = "";
    public string LicenseEvidence { get; set; } = "Not verified until ZOS-API connects";
    public bool ApiFilesPresent => File.Exists(ZosApiPath) && File.Exists(ZosApiInterfacesPath) && File.Exists(NetHelperPath);

    public static List<ZemaxInstallation> FindAll() => ZemaxDiscovery.FindAll();
    public static ZemaxInstallation? FromFolder(string folder) => ZemaxDiscovery.FromFolder(folder);
}

internal static class ZemaxDiscovery
{
    private static readonly string[] NetHelperRelativePaths =
    {
        "ZOSAPI_NetHelper.dll",
        @"ZOS-API\Libraries\ZOSAPI_NetHelper.dll",
        @"ZOS_API\Libraries\ZOSAPI_NetHelper.dll"
    };

    public static List<ZemaxInstallation> FindAll()
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddCandidate(candidates, Environment.GetEnvironmentVariable("ZEMAX_ROOT"), "ZEMAX_ROOT environment variable");
        AddUninstallCandidates(candidates);
        AddProductRegistryCandidates(candidates);
        AddKnownProgramFolders(candidates);

        var dataDirectory = FindDataDirectory(out var dataDirectorySource);
        return candidates
            .Select(x => CreateInstallation(x.Key, x.Value, dataDirectory, dataDirectorySource))
            .Where(x => x != null)
            .Cast<ZemaxInstallation>()
            .OrderByDescending(x => ExtractVersion(x.DisplayName))
            .ThenBy(x => x.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ZemaxInstallation? FromFolder(string folder)
    {
        var dataDirectory = FindDataDirectory(out var dataDirectorySource);
        return CreateInstallation(folder, "manually selected folder", dataDirectory, dataDirectorySource);
    }

    private static ZemaxInstallation? CreateInstallation(string rawRoot, string source, string dataDirectory, string dataDirectorySource)
    {
        try
        {
            var root = NormalizeProgramRoot(rawRoot);
            if (root == null) return null;
            var zosApi = Path.Combine(root, "ZOSAPI.dll");
            var interfaces = Path.Combine(root, "ZOSAPI_Interfaces.dll");
            var netHelper = NetHelperRelativePaths.Select(x => Path.Combine(root, x)).FirstOrDefault(File.Exists);
            if (!File.Exists(zosApi) || !File.Exists(interfaces) || netHelper == null) return null;

            var executable = new[] { "OpticStudio.exe", "Zemax.exe" }
                .Select(x => Path.Combine(root, x)).FirstOrDefault(File.Exists) ?? "";
            var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return new ZemaxInstallation
            {
                Root = root,
                DisplayName = name + " — " + root,
                DiscoverySource = source,
                ZosApiPath = zosApi,
                ZosApiInterfacesPath = interfaces,
                NetHelperPath = netHelper,
                OpticStudioExecutablePath = executable,
                DataDirectory = dataDirectory,
                DataDirectorySource = dataDirectorySource,
                LicenseEvidence = DescribeLicenseEvidence(dataDirectory)
            };
        }
        catch { return null; }
    }

    private static string? NormalizeProgramRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
        if (!Directory.Exists(path)) return null;
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(Path.Combine(path, "ZOSAPI.dll"))) return path;

        // Registry uninstall entries sometimes point one level above the
        // actual product folder. Keep this bounded to known product names.
        foreach (var childName in new[] { "Zemax OpticStudio", "Ansys Zemax OpticStudio", "OpticStudio" })
        {
            var child = Path.Combine(path, childName);
            if (File.Exists(Path.Combine(child, "ZOSAPI.dll"))) return child;
        }
        return null;
    }

    private static void AddKnownProgramFolders(IDictionary<string, string> candidates)
    {
        foreach (var programFiles in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "Zemax OpticStudio", "Ansys Zemax OpticStudio", "OpticStudio" })
                AddCandidate(candidates, Path.Combine(programFiles, name), "known Program Files location");

            AddMatchingChildren(candidates, programFiles, "*Zemax*", "Program Files product folder");
            AddMatchingChildren(candidates, programFiles, "*OpticStudio*", "Program Files product folder");

            var ansysRoot = Path.Combine(programFiles, "ANSYS Inc");
            if (!Directory.Exists(ansysRoot)) continue;
            foreach (var versionFolder in SafeDirectories(ansysRoot, "v*"))
            {
                AddCandidate(candidates, versionFolder, "Ansys version folder");
                foreach (var name in new[] { "Zemax OpticStudio", "OpticStudio" })
                    AddCandidate(candidates, Path.Combine(versionFolder, name), "Ansys version folder");
                AddMatchingChildren(candidates, versionFolder, "*Zemax*", "Ansys version product folder");
                AddMatchingChildren(candidates, versionFolder, "*OpticStudio*", "Ansys version product folder");
            }
        }
    }

    private static void AddUninstallCandidates(IDictionary<string, string> candidates)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null) continue;
                foreach (var subName in uninstall.GetSubKeyNames())
                {
                    using var product = uninstall.OpenSubKey(subName);
                    var displayName = product?.GetValue("DisplayName") as string ?? "";
                    if (displayName.IndexOf("OpticStudio", StringComparison.OrdinalIgnoreCase) < 0 &&
                        displayName.IndexOf("Zemax", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    AddCandidate(candidates, product?.GetValue("InstallLocation") as string, "Windows uninstall registry");
                    AddCandidate(candidates, product?.GetValue("DisplayIcon") as string, "Windows uninstall registry");
                }
            }
            catch { /* Registry visibility differs by policy and process bitness. */ }
        }
    }

    private static void AddProductRegistryCandidates(IDictionary<string, string> candidates)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var keyPath in new[] { @"SOFTWARE\Zemax", @"SOFTWARE\Ansys\Zemax", @"SOFTWARE\ANSYS, Inc.\Zemax" })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(keyPath);
                CollectRegistryPaths(key, candidates, 0);
            }
            catch { }
        }
    }

    private static void CollectRegistryPaths(RegistryKey? key, IDictionary<string, string> candidates, int depth)
    {
        if (key == null || depth > 6) return;
        foreach (var name in key.GetValueNames())
            if (key.GetValue(name) is string value) AddCandidate(candidates, value, "Zemax product registry");
        foreach (var childName in key.GetSubKeyNames())
        {
            try { using var child = key.OpenSubKey(childName); CollectRegistryPaths(child, candidates, depth + 1); }
            catch { }
        }
    }

    private static string FindDataDirectory(out string source)
    {
        var candidates = new List<KeyValuePair<string, string>>();
        AddDataCandidate(candidates, Environment.GetEnvironmentVariable("ZEMAX_DATA_ROOT"), "ZEMAX_DATA_ROOT environment variable");
        AddRegistryDataCandidates(candidates);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents)) AddDataCandidate(candidates, Path.Combine(documents, "Zemax"), "Windows Documents folder");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) AddDataCandidate(candidates, Path.Combine(profile, "Documents", "Zemax"), "default profile Documents folder");
        AddDataCandidate(candidates, @"P:\Zemax", "OpticStudio Online default");

        // A redirected Documents folder is common on managed work PCs. Prefer
        // an explicitly configured root, then one containing recognizable data.
        var distinct = candidates.GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
        var found = distinct.FirstOrDefault(x => IsZemaxDataDirectory(x.Key));
        if (string.IsNullOrWhiteSpace(found.Key)) found = distinct.FirstOrDefault(x => Directory.Exists(x.Key));
        if (string.IsNullOrWhiteSpace(found.Key)) found = distinct.FirstOrDefault();
        source = found.Value ?? "";
        return found.Key ?? "";
    }

    private static void AddRegistryDataCandidates(ICollection<KeyValuePair<string, string>> candidates)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        foreach (var keyPath in new[] { @"SOFTWARE\Zemax", @"SOFTWARE\Ansys\Zemax", @"SOFTWARE\ANSYS, Inc.\Zemax" })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                using var key = baseKey.OpenSubKey(keyPath);
                AddDataCandidate(candidates, key?.GetValue("ZemaxRoot") as string, "OpticStudio user registry");
            }
            catch { }
        }
    }

    private static void AddDataCandidate(ICollection<KeyValuePair<string, string>> candidates, string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path!.Trim().Trim('"')))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            candidates.Add(new KeyValuePair<string, string>(expanded, source));
        }
        catch { }
    }

    private static bool IsZemaxDataDirectory(string path) => Directory.Exists(path) &&
        new[] { "Configs", "License", "ZOS-API", "ZOS_API", "Glass", "Lenses" }.Any(x => Directory.Exists(Path.Combine(path, x)));

    private static string DescribeLicenseEvidence(string dataDirectory)
    {
        var evidence = new List<string>();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANSYSLMD_LICENSE_FILE")))
            evidence.Add("Ansys license environment configured");
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            var licenseFolder = Path.Combine(dataDirectory, "License");
            if (Directory.Exists(licenseFolder)) evidence.Add("license data folder found");
            if (File.Exists(Path.Combine(dataDirectory, "Configs", "SNTLCONFIG.XML")))
                evidence.Add("Zemax network-license configuration found");
        }
        return evidence.Count == 0 ? "Not pre-detected; ZOS-API will verify at runtime" : string.Join("; ", evidence);
    }

    private static void AddMatchingChildren(IDictionary<string, string> candidates, string parent, string pattern, string source)
    {
        foreach (var folder in SafeDirectories(parent, pattern)) AddCandidate(candidates, folder, source);
    }

    private static IEnumerable<string> SafeDirectories(string parent, string pattern)
    {
        try { return Directory.Exists(parent) ? Directory.GetDirectories(parent, pattern, SearchOption.TopDirectoryOnly) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static void AddCandidate(IDictionary<string, string> candidates, string? path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            path = Environment.ExpandEnvironmentVariables(path!.Trim());
            var comma = path.LastIndexOf(',');
            if (comma > 2 && int.TryParse(path.Substring(comma + 1), out _)) path = path.Substring(0, comma);
            path = path.Trim().Trim('"');
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? "";
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            path = Path.GetFullPath(path);
            if (!candidates.ContainsKey(path)) candidates[path] = source;
        }
        catch { }
    }

    private static Version ExtractVersion(string text)
    {
        var ansysFolder = Regex.Match(text, @"(?i)(?:^|[\\/\s_-])v(?<major>\d{2,4})(?:[._-](?<minor>\d+))?");
        if (ansysFolder.Success && int.TryParse(ansysFolder.Groups["major"].Value, out var ansysMajor))
        {
            int.TryParse(ansysFolder.Groups["minor"].Value, out var ansysMinor);
            return new Version(ansysMajor, ansysMinor);
        }

        var namedRelease = Regex.Match(text, @"(?i)(?<year>20\d{2})\s*R(?<release>\d+)");
        if (namedRelease.Success && int.TryParse(namedRelease.Groups["year"].Value, out var year) &&
            int.TryParse(namedRelease.Groups["release"].Value, out var release)) return new Version(year, release);

        var dotted = Regex.Match(text, @"(?<!\d)(?<version>\d+\.\d+(?:\.\d+){0,2})(?!\d)");
        if (dotted.Success && Version.TryParse(dotted.Groups["version"].Value, out var version)) return version;
        return new Version(0, 0);
    }
}
