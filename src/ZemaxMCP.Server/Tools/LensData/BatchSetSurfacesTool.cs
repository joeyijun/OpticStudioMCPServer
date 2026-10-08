using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public sealed class BatchSetSurfacesTool
{
    private const int MaximumEdits = 100;
    private readonly IZemaxSession _session;

    public BatchSetSurfacesTool(IZemaxSession session) => _session = session;

    public record SurfaceEdit(
        int SurfaceNumber,
        double? Radius = null,
        double? Thickness = null,
        string? Material = null,
        double? SemiDiameter = null,
        double? Conic = null,
        string? Comment = null,
        bool? IsStop = null);

    public record SurfaceState(
        int SurfaceNumber,
        double Radius,
        double Thickness,
        string Material,
        double SemiDiameter,
        double Conic,
        string Comment,
        bool IsStop);

    public record Result(
        bool Success,
        string? Error,
        int RequestedEdits,
        int AppliedEdits,
        bool RolledBack,
        IReadOnlyList<SurfaceReadback> Surfaces);

    [ZemaxTool(Name = "zemax_batch_set_surfaces")]
    [Description("Atomically modify geometry/material/comment/stop state on multiple sequential LDE surfaces. The request is fully validated before mutation, creates one safety snapshot, verifies readback, and restores every touched surface if any write/readback fails.")]
    public async Task<Result> ExecuteAsync(
        [Description("Surface edits. Surface numbers must be unique; at most 100 edits per call. Omitted properties remain unchanged.")] List<SurfaceEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var rolledBack = false;
        try
        {
            ValidateEdits(edits);
            cancellationToken.ThrowIfCancellationRequested();

            return await _session.ExecuteAsync(
                "BatchSetSurfaces",
                new Dictionary<string, object?> { ["editCount"] = edits.Count },
                system =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var lde = system.LDE;
                    var maxSurface = lde.NumberOfSurfaces - 1;

                    // Validate every target against the actual open model before
                    // the first write so an invalid late edit cannot create a
                    // partially modified optical system.
                    foreach (var edit in edits)
                    {
                        if (edit.SurfaceNumber < 0 || edit.SurfaceNumber > maxSurface)
                            throw new ArgumentOutOfRangeException(
                                nameof(edits),
                                $"Surface {edit.SurfaceNumber} does not exist. Valid range is 0-{maxSurface}.");
                    }

                    var originals = edits.ToDictionary(
                        edit => edit.SurfaceNumber,
                        edit => ReadState(lde.GetSurfaceAt(edit.SurfaceNumber), edit.SurfaceNumber));

                    var applied = new List<SurfaceReadback>(edits.Count);
                    try
                    {
                        foreach (var edit in edits)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var row = lde.GetSurfaceAt(edit.SurfaceNumber);

                            if (edit.Radius.HasValue) row.Radius = edit.Radius.Value;
                            if (edit.Thickness.HasValue) row.Thickness = edit.Thickness.Value;
                            if (edit.Material is not null) row.Material = edit.Material;
                            if (edit.SemiDiameter.HasValue) row.SemiDiameter = edit.SemiDiameter.Value;
                            if (edit.Conic.HasValue) row.Conic = edit.Conic.Value;
                            if (edit.Comment is not null) row.Comment = edit.Comment;
                            if (edit.IsStop.HasValue) row.IsStop = edit.IsStop.Value;
                        }

                        foreach (var edit in edits)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var state = ReadState(lde.GetSurfaceAt(edit.SurfaceNumber), edit.SurfaceNumber);
                            VerifyReadback(edit, state);
                            // Preserve raw originals for rollback, but never serialize COM infinity directly.
                            // Convert inside the transaction so invalid readbacks also trigger rollback.
                            applied.Add(SurfaceReadback.FromRaw(state.SurfaceNumber, state.Radius, state.Thickness,
                                state.Material, state.SemiDiameter, state.Conic, state.Comment, state.IsStop));
                        }

                        return new Result(true, null, edits.Count, applied.Count, false, applied);
                    }
                    catch (Exception writeError)
                    {
                        Exception? rollbackError = null;
                        try
                        {
                            foreach (var pair in originals.OrderByDescending(pair => pair.Key))
                                RestoreState(lde.GetSurfaceAt(pair.Key), pair.Value);
                            rolledBack = true;
                        }
                        catch (Exception ex)
                        {
                            rollbackError = ex;
                        }

                        if (rollbackError != null)
                            throw new AggregateException(
                                "Batch surface modification failed and rollback also failed. The automatic pre-change snapshot is the recovery boundary.",
                                writeError,
                                rollbackError);
                        throw;
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
            return new Result(false, ex.Message, edits?.Count ?? 0, 0, rolledBack, Array.Empty<SurfaceReadback>());
        }
    }

    private static void ValidateEdits(List<SurfaceEdit>? edits)
    {
        if (edits == null || edits.Count == 0)
            throw new ArgumentException("At least one surface edit is required.", nameof(edits));
        if (edits.Count > MaximumEdits)
            throw new ArgumentException($"At most {MaximumEdits} surface edits are allowed per call.", nameof(edits));
        if (edits.Select(edit => edit.SurfaceNumber).Distinct().Count() != edits.Count)
            throw new ArgumentException("Each surface may appear only once in a batch.", nameof(edits));
        if (edits.Count(edit => edit.IsStop == true) > 1)
            throw new ArgumentException("A batch may designate at most one surface as the stop.", nameof(edits));

        foreach (var edit in edits)
        {
            if (!HasChange(edit))
                throw new ArgumentException($"Surface {edit.SurfaceNumber} contains no requested changes.", nameof(edits));
            ValidateFinite(edit.Radius, edit.SurfaceNumber, nameof(edit.Radius));
            ValidateFinite(edit.Thickness, edit.SurfaceNumber, nameof(edit.Thickness));
            ValidateFinite(edit.SemiDiameter, edit.SurfaceNumber, nameof(edit.SemiDiameter));
            ValidateFinite(edit.Conic, edit.SurfaceNumber, nameof(edit.Conic));
            if (edit.SemiDiameter.HasValue && edit.SemiDiameter.Value < 0)
                throw new ArgumentException($"Surface {edit.SurfaceNumber} semiDiameter cannot be negative.", nameof(edits));
        }
    }

    private static bool HasChange(SurfaceEdit edit) =>
        edit.Radius.HasValue || edit.Thickness.HasValue || edit.Material is not null ||
        edit.SemiDiameter.HasValue || edit.Conic.HasValue || edit.Comment is not null || edit.IsStop.HasValue;

    private static void ValidateFinite(double? value, int surfaceNumber, string name)
    {
        if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
            throw new ArgumentException($"Surface {surfaceNumber} {name} must be finite.");
    }

    private static SurfaceState ReadState(dynamic row, int surfaceNumber) => new(
        surfaceNumber,
        (double)row.Radius,
        (double)row.Thickness,
        (string)(row.Material ?? string.Empty),
        (double)row.SemiDiameter,
        (double)row.Conic,
        (string)(row.Comment ?? string.Empty),
        (bool)row.IsStop);

    private static void RestoreState(dynamic row, SurfaceState state)
    {
        row.Radius = state.Radius;
        row.Thickness = state.Thickness;
        row.Material = state.Material;
        row.SemiDiameter = state.SemiDiameter;
        row.Conic = state.Conic;
        row.Comment = state.Comment;
        row.IsStop = state.IsStop;
    }

    private static void VerifyReadback(SurfaceEdit edit, SurfaceState actual)
    {
        VerifyDouble(edit.Radius, actual.Radius.SanitizeRadius(), edit.SurfaceNumber, nameof(edit.Radius));
        VerifyDouble(edit.Thickness, actual.Thickness, edit.SurfaceNumber, nameof(edit.Thickness));
        VerifyDouble(edit.SemiDiameter, actual.SemiDiameter, edit.SurfaceNumber, nameof(edit.SemiDiameter));
        VerifyDouble(edit.Conic, actual.Conic, edit.SurfaceNumber, nameof(edit.Conic));

        if (edit.Material is not null &&
            !string.Equals(edit.Material.Trim(), actual.Material.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Surface {edit.SurfaceNumber} material readback '{actual.Material}' does not match requested '{edit.Material}'.");

        if (edit.Comment is not null && !string.Equals(edit.Comment, actual.Comment, StringComparison.Ordinal))
            throw new InvalidDataException($"Surface {edit.SurfaceNumber} comment readback does not match the requested value.");

        if (edit.IsStop.HasValue && edit.IsStop.Value != actual.IsStop)
            throw new InvalidDataException(
                $"Surface {edit.SurfaceNumber} stop-state readback {actual.IsStop} does not match requested {edit.IsStop.Value}.");
    }

    private static void VerifyDouble(double? requested, double actual, int surfaceNumber, string name)
    {
        if (!requested.HasValue) return;
        if (double.IsNaN(actual) || double.IsInfinity(actual))
            throw new InvalidDataException($"Surface {surfaceNumber} {name} returned a non-finite value for a finite requested edit.");
        var tolerance = Math.Max(1e-12, Math.Abs(requested.Value) * 1e-12);
        if (Math.Abs(requested.Value - actual) > tolerance)
            throw new InvalidDataException(
                $"Surface {surfaceNumber} {name} readback {actual:G17} does not match requested {requested.Value:G17}.");
    }
}
