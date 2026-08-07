using System.Globalization;

namespace ZemaxMCP.Core.Session;

internal sealed class ZemaxOperationSafety
{
    private const int MaximumSnapshots = 100;
    private readonly bool _readOnly;
    private readonly string _snapshotDirectory;

    public ZemaxOperationSafety()
    {
        var readOnly = Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY");
        _readOnly = string.Equals(readOnly, "1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(readOnly, "true", StringComparison.OrdinalIgnoreCase);
        _snapshotDirectory = Environment.GetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZemaxMCP", "snapshots");
    }

    public bool ReadOnly => _readOnly;
    public string SnapshotDirectory => _snapshotDirectory;
    public string? LastSnapshotPath { get; private set; }

    public void BeforeOperation(IZosSystemSnapshot system, string commandName)
    {
        if (!RequiresSnapshot(commandName)) return;
        if (_readOnly)
            throw new InvalidOperationException("Read-only mode blocked the mutating Zemax operation '" + commandName + "'. Disable read-only mode in the launcher to allow lens changes.");
        LastSnapshotPath = CreateSnapshot(system, commandName);
    }

    internal static bool RequiresSnapshot(string commandName) =>
        ZemaxOperationMetadata.GetCommandImpact(commandName) == ZemaxOperationImpact.HighImpact;

    private string CreateSnapshot(IZosSystemSnapshot system, string commandName)
    {
        Directory.CreateDirectory(_snapshotDirectory);
        var sourceName = string.IsNullOrWhiteSpace(system.SystemFile) ? "Unsaved" : Path.GetFileNameWithoutExtension(system.SystemFile);
        var safeName = Sanitize(sourceName ?? "Lens");
        var safeCommand = Sanitize(commandName);
        var fileName = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "_" + safeName + "_before-" + safeCommand + ".zos";
        var path = Path.Combine(_snapshotDirectory, fileName);
        IZosSystemSnapshot? copy = null;
        try
        {
            copy = system.CopySystem() ?? throw new InvalidOperationException("OpticStudio did not create a system copy for the safety snapshot.");
            copy.SaveAs(path);
            if (!File.Exists(path)) throw new IOException("OpticStudio did not write the safety snapshot: " + path);
        }
        finally
        {
            try { copy?.Close(false); } catch { }
        }
        PruneOldSnapshots();
        return path;
    }

    private void PruneOldSnapshots()
    {
        try
        {
            foreach (var file in new DirectoryInfo(_snapshotDirectory).GetFiles("*.zos")
                         .OrderByDescending(x => x.LastWriteTimeUtc).Skip(MaximumSnapshots)) file.Delete();
        }
        catch { /* Snapshot retention must not invalidate a successfully created snapshot. */ }
    }

    private static string Sanitize(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '_');
        return string.IsNullOrWhiteSpace(value) ? "Lens" : value;
    }
}
