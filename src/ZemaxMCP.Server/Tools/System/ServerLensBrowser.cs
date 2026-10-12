using System.Diagnostics;

namespace ZemaxMCP.Server.Tools.System;

internal static class ServerLensBrowser
{
    internal sealed record Entry(string Name, string Kind, string RelativePath, string ServerPath);
    internal sealed record Result(bool Success, string? Code, string? Error,
        string ExecutionComputer, string PathNamespace, string Root, string? ServerDirectory,
        IReadOnlyList<Entry> Entries, bool Truncated, int ScannedEntries, string Guidance);

    private const int MaximumScannedEntries = 2000;
    private const string Guidance = "Single-directory metadata listing on the OpticStudio computer; only .zos/.zmx files and non-link directories are returned. No contents are read or transferred. If truncated, choose a narrower subdirectory; this is not a complete inventory.";

    internal static Result List(string root, string relativeDirectory, int maxEntries,
        string? dataDirectory, string? currentFile, CancellationToken cancellationToken)
    {
        string? directory = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maxEntries < 1 || maxEntries > 200)
                throw new ArgumentException("maxEntries must be between 1 and 200.");
            if (relativeDirectory == null || relativeDirectory.Length > 512)
                throw new ArgumentException("relativeDirectory must be a relative path of at most 512 characters.");
            var selected = (root ?? "").ToLowerInvariant();
            string? rootPath = selected switch
            {
                "samples" => string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, "Samples"),
                "lenses" => string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, "Lens"),
                "current" => string.IsNullOrWhiteSpace(currentFile) ? null : Path.GetDirectoryName(currentFile),
                _ => throw new ArgumentException("root must be samples, lenses or current. Arbitrary server roots are not accepted.")
            };
            if (string.IsNullOrWhiteSpace(rootPath))
                return Failure("server_root_unavailable", "The requested root has not been reported by the OpticStudio Worker. Check zemax_execution_context; do not search the AI computer instead.");

            var parts = relativeDirectory.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (Path.IsPathRooted(relativeDirectory) || relativeDirectory.Contains(':') || parts.Length > 16 ||
                parts.Any(part => part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new ArgumentException("Use only ordinary relative directory names; rooted, traversal, wildcard and alternate-stream paths are not allowed.");
            var canonicalRoot = Path.GetFullPath(rootPath);
            if (!Path.IsPathRooted(rootPath) || canonicalRoot.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("The bounded browser only accepts absolute local-drive roots on the OpticStudio computer, not network shares.");
            directory = Path.GetFullPath(Path.Combine(canonicalRoot, Path.Combine(parts.Length == 0 ? new[] { "" } : parts)));
            var boundary = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!directory.Equals(boundary, StringComparison.OrdinalIgnoreCase) &&
                !directory.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Directory is outside the selected server root.");
            EnsureNoReparseAncestors(directory);
            if (!Directory.Exists(directory))
                return Failure("server_path_not_found", "Directory not found on the OpticStudio computer. It was not checked on the AI client.");

            var entries = new List<Entry>();
            var timer = Stopwatch.StartNew();
            var scanned = 0;
            var truncated = false;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scanned >= MaximumScannedEntries || timer.Elapsed > TimeSpan.FromSeconds(2))
                { truncated = true; break; }
                scanned++;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var extension = Path.GetExtension(path);
                if (!isDirectory && !extension.Equals(".zos", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".zmx", StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(path);
                entries.Add(new Entry(name, isDirectory ? "directory" : "lens-file",
                    string.IsNullOrEmpty(relativeDirectory) ? name : Path.Combine(relativeDirectory, name), path));
            }
            return new Result(true, null, null, Environment.MachineName,
                ZemaxMCP.ToolManifest.ExecutionLocationPolicy.PathNamespace, selected, directory,
                entries.OrderBy(e => e.Kind).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(maxEntries).ToArray(),
                truncated || entries.Count > maxEntries, scanned, Guidance);
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException ex) { return Failure("path_not_allowed", ex.Message); }
        catch (UnauthorizedAccessException) { return Failure("server_access_denied", "The OpticStudio Worker's Windows account cannot access this directory."); }
        catch (IOException ex) { return Failure("server_file_error", ex.Message); }

        Result Failure(string code, string error) => new(false, code, error, Environment.MachineName,
            ZemaxMCP.ToolManifest.ExecutionLocationPolicy.PathNamespace, root ?? "", directory,
            Array.Empty<Entry>(), false, 0, Guidance);
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        for (var ancestor = new DirectoryInfo(path); ancestor != null; ancestor = ancestor.Parent)
        {
            if (!ancestor.Exists) continue;
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Reparse-point / symbolic-link directories are not permitted by the server browser.");
        }
    }
}
