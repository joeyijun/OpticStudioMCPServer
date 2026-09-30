using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public sealed class SnapshotTools
{
    private const int MaximumListedSnapshots = 100;
    private const int MaximumDifferences = 200;
    private const int MaximumSurfaceScan = 2000;
    private readonly IZemaxSession _session;

    public SnapshotTools(IZemaxSession session) => _session = session;

    public record SnapshotInfo(
        string FileName,
        string Extension,
        long SizeBytes,
        DateTimeOffset LastWriteTimeUtc,
        bool IsLatest);

    public record ListResult(
        bool Success,
        string? Error,
        string SnapshotDirectory,
        int TotalSnapshots,
        IReadOnlyList<SnapshotInfo> Snapshots);

    public record PropertyDifference(
        int SurfaceNumber,
        string Property,
        string CurrentValue,
        string SnapshotValue);

    public record DiffResult(
        bool Success,
        string? Error,
        string SnapshotFileName,
        string? CurrentFilePath,
        int CurrentSurfaceCount,
        int SnapshotSurfaceCount,
        int InspectedSurfaces,
        int ObservedPropertyDifferences,
        bool ScanTruncated,
        bool DifferencesTruncated,
        IReadOnlyList<PropertyDifference> Differences);

    public record RestoreResult(
        bool Success,
        string? Error,
        string SnapshotFileName,
        string? ProtectedCurrentSnapshotFileName,
        string? WorkingFilePath);

    [ZemaxTool(Name = "zemax_snapshot_list")]
    [Description("List recent pre-change Zemax safety snapshots from the configured snapshot directory. Returns snapshot file names for use with snapshot diff/restore; no optical-system mutation occurs.")]
    public ListResult List(
        [Description("Maximum snapshots to return (1-100), newest first")] int limit = 25)
    {
        try
        {
            if (limit is < 1 or > MaximumListedSnapshots)
                throw new ArgumentOutOfRangeException(nameof(limit), $"limit must be between 1 and {MaximumListedSnapshots}.");

            var directory = Path.GetFullPath(_session.SnapshotDirectory);
            if (!Directory.Exists(directory))
                return new ListResult(true, null, directory, 0, Array.Empty<SnapshotInfo>());

            var files = new DirectoryInfo(directory)
                .GetFiles()
                .Where(IsSnapshotFile)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .ToArray();

            var latest = _session.LastSnapshotPath == null ? null : Path.GetFullPath(_session.LastSnapshotPath);
            var snapshots = files.Take(limit).Select(file => new SnapshotInfo(
                file.Name,
                file.Extension,
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                latest != null && string.Equals(file.FullName, latest, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            return new ListResult(true, null, directory, files.Length, snapshots);
        }
        catch (Exception ex)
        {
            return new ListResult(false, ex.Message, _session.SnapshotDirectory, 0, Array.Empty<SnapshotInfo>());
        }
    }

    [ZemaxTool(Name = "zemax_snapshot_diff")]
    [Description("Compare the current sequential LDE with one safety snapshot without replacing the active model. The snapshot is loaded only into a temporary copied optical system; changed surface parameters are returned with bounded output.")]
    public async Task<DiffResult> DiffAsync(
        [Description("Snapshot file name returned by zemax_snapshot_list; paths are not accepted")] string snapshotFileName,
        [Description("Maximum property differences to return (1-200)")] int maxDifferences = 50,
        [Description("Maximum corresponding LDE surfaces to inspect (1-2000)")] int maxSurfaces = 500,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (maxDifferences is < 1 or > MaximumDifferences)
                throw new ArgumentOutOfRangeException(nameof(maxDifferences), $"maxDifferences must be between 1 and {MaximumDifferences}.");
            if (maxSurfaces is < 1 or > MaximumSurfaceScan)
                throw new ArgumentOutOfRangeException(nameof(maxSurfaces), $"maxSurfaces must be between 1 and {MaximumSurfaceScan}.");

            var snapshotPath = ResolveSnapshot(snapshotFileName);
            return await _session.ExecuteAsync(
                "SnapshotDiff",
                new Dictionary<string, object?>
                {
                    ["SnapshotFileName"] = snapshotFileName,
                    ["MaxDifferences"] = maxDifferences,
                    ["MaxSurfaces"] = maxSurfaces
                },
                system =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (system.Mode == ZOSAPI.SystemType.NonSequential)
                        throw new NotSupportedException("Snapshot parameter diff currently supports sequential systems only.");

                    var comparison = system.CopySystem()
                        ?? throw new InvalidOperationException("OpticStudio could not create a temporary system copy for snapshot comparison.");
                    try
                    {
                        if (!comparison.LoadFile(snapshotPath, false))
                            throw new IOException("OpticStudio did not load the requested snapshot into the comparison system.");
                        if (comparison.Mode == ZOSAPI.SystemType.NonSequential)
                            throw new NotSupportedException("The selected snapshot is non-sequential; sequential LDE comparison is not applicable.");

                        var currentLde = system.LDE;
                        var snapshotLde = comparison.LDE;
                        var comparable = Math.Min(currentLde.NumberOfSurfaces, snapshotLde.NumberOfSurfaces);
                        var inspected = Math.Min(comparable, maxSurfaces);
                        var differences = new List<PropertyDifference>(Math.Min(maxDifferences, 64));
                        var observed = 0;

                        if (currentLde.NumberOfSurfaces != snapshotLde.NumberOfSurfaces)
                            AddDifference(
                                differences, maxDifferences, ref observed, -1, "surfaceCount",
                                currentLde.NumberOfSurfaces.ToString(CultureInfo.InvariantCulture),
                                snapshotLde.NumberOfSurfaces.ToString(CultureInfo.InvariantCulture));

                        for (var surfaceNumber = 0; surfaceNumber < inspected; surfaceNumber++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var current = currentLde.GetSurfaceAt(surfaceNumber);
                            var snapshot = snapshotLde.GetSurfaceAt(surfaceNumber);

                            CompareDouble(differences, maxDifferences, ref observed, surfaceNumber, "radius", current.Radius, snapshot.Radius);
                            CompareDouble(differences, maxDifferences, ref observed, surfaceNumber, "thickness", current.Thickness, snapshot.Thickness);
                            CompareString(differences, maxDifferences, ref observed, surfaceNumber, "material", current.Material, snapshot.Material, ignoreCase: true);
                            CompareDouble(differences, maxDifferences, ref observed, surfaceNumber, "semiDiameter", current.SemiDiameter, snapshot.SemiDiameter);
                            CompareDouble(differences, maxDifferences, ref observed, surfaceNumber, "conic", current.Conic, snapshot.Conic);
                            CompareString(differences, maxDifferences, ref observed, surfaceNumber, "comment", current.Comment, snapshot.Comment, ignoreCase: false);
                            CompareString(differences, maxDifferences, ref observed, surfaceNumber, "surfaceType", current.Type.ToString(), snapshot.Type.ToString(), ignoreCase: false);
                            CompareBool(differences, maxDifferences, ref observed, surfaceNumber, "isStop", current.IsStop, snapshot.IsStop);
                            CompareString(differences, maxDifferences, ref observed, surfaceNumber, "radiusSolve", current.RadiusCell.Solve.ToString(), snapshot.RadiusCell.Solve.ToString(), ignoreCase: false);
                            CompareString(differences, maxDifferences, ref observed, surfaceNumber, "thicknessSolve", current.ThicknessCell.Solve.ToString(), snapshot.ThicknessCell.Solve.ToString(), ignoreCase: false);
                            CompareString(differences, maxDifferences, ref observed, surfaceNumber, "conicSolve", current.ConicCell.Solve.ToString(), snapshot.ConicCell.Solve.ToString(), ignoreCase: false);
                            CompareString(differences, maxDifferences, ref observed, surfaceNumber, "materialSolve", current.MaterialCell.Solve.ToString(), snapshot.MaterialCell.Solve.ToString(), ignoreCase: false);
                        }

                        var scanTruncated = comparable > inspected;
                        return new DiffResult(
                            true,
                            null,
                            snapshotFileName,
                            string.IsNullOrWhiteSpace(system.SystemFile) ? null : system.SystemFile,
                            currentLde.NumberOfSurfaces,
                            snapshotLde.NumberOfSurfaces,
                            inspected,
                            observed,
                            scanTruncated,
                            scanTruncated || observed > differences.Count,
                            differences);
                    }
                    finally
                    {
                        comparison.Close(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DiffResult(
                false, ex.Message, snapshotFileName ?? string.Empty, _session.CurrentFilePath,
                0, 0, 0, 0, false, false, Array.Empty<PropertyDifference>());
        }
    }

    [ZemaxTool(Name = "zemax_snapshot_restore")]
    [Description("Restore a safety snapshot into a new working copy. Before loading it, the normal HighImpact safety gate snapshots the current optical system, so the current state is protected and the historical snapshot itself is never opened as the writable working file.")]
    public async Task<RestoreResult> RestoreAsync(
        [Description("Snapshot file name returned by zemax_snapshot_list; paths are not accepted")] string snapshotFileName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotPath = ResolveSnapshot(snapshotFileName);
            var previousSafetySnapshot = _session.LastSnapshotPath;
            var workingPath = await _session.RestoreSnapshotAsync(snapshotPath, cancellationToken).ConfigureAwait(false);
            var protectedCurrent = _session.LastSnapshotPath;

            if (string.IsNullOrWhiteSpace(protectedCurrent) ||
                string.Equals(previousSafetySnapshot, protectedCurrent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Snapshot restore completed without reporting a new pre-restore safety snapshot.");

            return new RestoreResult(
                true,
                null,
                snapshotFileName,
                Path.GetFileName(protectedCurrent),
                workingPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RestoreResult(false, ex.Message, snapshotFileName ?? string.Empty, null, null);
        }
    }

    private string ResolveSnapshot(string snapshotFileName)
    {
        if (string.IsNullOrWhiteSpace(snapshotFileName))
            throw new ArgumentException("snapshotFileName is required.", nameof(snapshotFileName));
        if (!string.Equals(Path.GetFileName(snapshotFileName), snapshotFileName, StringComparison.Ordinal) ||
            snapshotFileName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
            snapshotFileName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            throw new ArgumentException("snapshotFileName must be a file name returned by zemax_snapshot_list, not a path.", nameof(snapshotFileName));

        var extension = Path.GetExtension(snapshotFileName);
        if (!extension.Equals(".zmx", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".zos", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only .zmx or .zos safety snapshots may be used.", nameof(snapshotFileName));

        var directory = Path.GetFullPath(_session.SnapshotDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(directory, snapshotFileName));
        var prefix = directory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Snapshot path escaped the configured snapshot directory.");
        if (!File.Exists(candidate))
            throw new FileNotFoundException("The requested safety snapshot does not exist.", candidate);

        var info = new FileInfo(candidate);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Snapshot reparse points are not accepted.");
        return candidate;
    }

    private static bool IsSnapshotFile(FileInfo file) =>
        (file.Extension.Equals(".zmx", StringComparison.OrdinalIgnoreCase) ||
         file.Extension.Equals(".zos", StringComparison.OrdinalIgnoreCase)) &&
        (file.Attributes & FileAttributes.ReparsePoint) == 0;

    private static void CompareDouble(
        List<PropertyDifference> output,
        int limit,
        ref int observed,
        int surfaceNumber,
        string property,
        double current,
        double snapshot)
    {
        if (Equivalent(current, snapshot)) return;
        AddDifference(output, limit, ref observed, surfaceNumber, property, FormatDouble(current), FormatDouble(snapshot));
    }

    private static void CompareString(
        List<PropertyDifference> output,
        int limit,
        ref int observed,
        int surfaceNumber,
        string property,
        string? current,
        string? snapshot,
        bool ignoreCase)
    {
        var left = current ?? string.Empty;
        var right = snapshot ?? string.Empty;
        if (string.Equals(left, right, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return;
        AddDifference(output, limit, ref observed, surfaceNumber, property, left, right);
    }

    private static void CompareBool(
        List<PropertyDifference> output,
        int limit,
        ref int observed,
        int surfaceNumber,
        string property,
        bool current,
        bool snapshot)
    {
        if (current == snapshot) return;
        AddDifference(
            output, limit, ref observed, surfaceNumber, property,
            current.ToString(CultureInfo.InvariantCulture),
            snapshot.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddDifference(
        List<PropertyDifference> output,
        int limit,
        ref int observed,
        int surfaceNumber,
        string property,
        string current,
        string snapshot)
    {
        observed++;
        if (output.Count < limit)
            output.Add(new PropertyDifference(surfaceNumber, property, current, snapshot));
    }

    private static bool Equivalent(double left, double right)
    {
        if (double.IsNaN(left) || double.IsNaN(right))
            return double.IsNaN(left) && double.IsNaN(right);
        if (double.IsInfinity(left) || double.IsInfinity(right))
            return left.Equals(right);
        var tolerance = Math.Max(1e-12, Math.Max(Math.Abs(left), Math.Abs(right)) * 1e-12);
        return Math.Abs(left - right) <= tolerance;
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value)) return "NaN";
        if (double.IsPositiveInfinity(value)) return "+Infinity";
        if (double.IsNegativeInfinity(value)) return "-Infinity";
        return value.ToString("G17", CultureInfo.InvariantCulture);
    }
}
