global using FrameworkCompatibility = ZemaxMCP.Server.Compatibility.FrameworkCompatibility;
global using ZemaxMCP.Server.Compatibility;

using System.Security.Cryptography;

namespace ZemaxMCP.Server.Compatibility;

// Shared by the actual net48 Worker and the vendor-independent test projections.
internal static class FrameworkCompatibility
{
    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    internal static string Sha256Hex(Stream input)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
    }

    internal static bool IsFullyQualifiedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Windows drive-absolute and UNC paths; reject C:relative and \root-relative.
        if (path!.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' &&
            (path[2] == '\\' || path[2] == '/')) return true;
        if (path.Length < 5 || !path.StartsWith("\\\\", StringComparison.Ordinal)) return false;
        var separator = path.IndexOf('\\', 2);
        return separator > 2 && separator < path.Length - 1;
    }
}

#if !NET5_0_OR_GREATER
internal static class FrameworkDictionaryExtensions
{
    internal static bool TryAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
    {
        if (dictionary.ContainsKey(key)) return false;
        dictionary.Add(key, value);
        return true;
    }
}
#endif
