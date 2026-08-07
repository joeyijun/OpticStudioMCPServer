using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SetExtraDataTool
{
    private readonly IZemaxSession _session;

    public SetExtraDataTool(IZemaxSession session) => _session = session;

    public record ExtraDataEntry(int Cell, double Value, bool IsVariable);

    public record SetExtraDataResult(
        bool Success,
        string? Error = null,
        int SurfaceNumber = 0,
        string? SurfaceType = null,
        string? AccessPath = null,
        ExtraDataEntry[]? Entries = null);

    [ZemaxTool(Name = "zemax_set_extra_data")]
    [Description(
        "Write cells in the Extra Data Editor (XDAT) for a surface. "
        + "Three modes: "
        + "(1) single set: provide cell + value; "
        + "(2) batch set: provide batchSet='3:0.1,4:-0.05,11:0.08'; "
        + "(3) variable flag: provide variableCells='3,4,11' to mark cells as optimization variables "
        + "(works standalone or combined with value writes). "
        + "Returns the readback of all modified cells.")]
    public async Task<SetExtraDataResult> ExecuteAsync(
        [Description("Surface number")] int surfaceNumber,
        [Description("Single set: cell number (1-indexed); 0 = not used")] int cell = 0,
        [Description("Single set: value to write")] double? value = null,
        [Description("Single set: mark this cell as Variable after write")] bool? makeVariable = null,
        [Description("Batch set: comma-separated 'cell:value' pairs, e.g. '3:0.1,4:-0.05'")] string? batchSet = null,
        [Description("Batch mark as Variable: comma-separated cell numbers, e.g. '3,4,11'")] string? variableCells = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (cell < 0)
                throw new ArgumentOutOfRangeException(nameof(cell), "XDAT cell number cannot be negative; use 0 only when the single-set mode is not used.");
            if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
                throw new ArgumentException("Single-set XDAT value must be finite.", nameof(value));
            if (cell == 0 && (value.HasValue || makeVariable == true))
                throw new ArgumentException("A positive cell number is required when value or makeVariable is supplied.", nameof(cell));

            var parameters = new Dictionary<string, object?>
            {
                ["surfaceNumber"] = surfaceNumber,
                ["cell"] = cell,
                ["value"] = value,
                ["batchSet"] = batchSet,
                ["variableCells"] = variableCells
            };

            return await _session.ExecuteAsync("SetExtraData", parameters, system =>
            {
                var lde = system.LDE;
                if (surfaceNumber < 0 || surfaceNumber >= lde.NumberOfSurfaces)
                    return new SetExtraDataResult(false,
                        Error: $"Invalid surface number: {surfaceNumber}. Valid range: 0-{lde.NumberOfSurfaces - 1}.");

                var surface = lde.GetSurfaceAt(surfaceNumber);
                var surfType = surface.Type.ToString();
                var writes = new List<(int cellNum, double val)>();
                var varMarks = new HashSet<int>();
                ExtraDataHelper.AccessPath pathUsed = ExtraDataHelper.AccessPath.Unknown;

                if (!string.IsNullOrWhiteSpace(batchSet))
                {
                    foreach (var pair in batchSet.Split(','))
                    {
                        var parts = pair.Trim().Split(':');
                        if (parts.Length != 2)
                            return new SetExtraDataResult(false,
                                Error: $"Invalid batchSet entry: '{pair}'. Expected 'cell:value'.");

                        if (!int.TryParse(parts[0].Trim(), out var cn) || cn <= 0 ||
                            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cv) ||
                            double.IsNaN(cv) || double.IsInfinity(cv))
                            return new SetExtraDataResult(false,
                                Error: $"Cannot parse batchSet entry '{pair}' as a positive cell number and finite value.");
                        writes.Add((cn, cv));
                    }
                }

                if (cell > 0 && value.HasValue)
                    writes.Add((cell, value.Value));

                if (!string.IsNullOrWhiteSpace(variableCells))
                {
                    foreach (var tok in variableCells.Split(','))
                    {
                        if (!int.TryParse(tok.Trim(), out var cn) || cn <= 0)
                            return new SetExtraDataResult(false,
                                Error: $"variableCells entry '{tok}' must be a positive cell number.");
                        varMarks.Add(cn);
                    }
                }

                if (makeVariable == true && cell > 0) varMarks.Add(cell);

                if (writes.Count == 0 && varMarks.Count == 0)
                    return new SetExtraDataResult(false,
                        Error: "No writes requested. Provide cell+value, batchSet, or variableCells.");

                var touched = new SortedSet<int>();
                foreach (var (cn, cv) in writes)
                {
                    var xcell = ExtraDataHelper.TryGetZernikeCell(surface, cn, out var path);
                    if (xcell == null && path != ExtraDataHelper.AccessPath.ZernikeTypedInterface)
                        xcell = ExtraDataHelper.TryGetCell(surface, cn, out path);
                    if (xcell == null)
                        return new SetExtraDataResult(false,
                            Error: $"Cannot access XDAT cell {cn} on surface {surfaceNumber} (type {surfType}).");
                    if (pathUsed == ExtraDataHelper.AccessPath.Unknown) pathUsed = path;

                    ExtraDataHelper.WriteValue(xcell, cv);
                    touched.Add(cn);
                }

                foreach (var cn in varMarks)
                {
                    var xcell = ExtraDataHelper.TryGetZernikeCell(surface, cn, out var path);
                    if (xcell == null && path != ExtraDataHelper.AccessPath.ZernikeTypedInterface)
                        xcell = ExtraDataHelper.TryGetCell(surface, cn, out path);
                    if (xcell == null)
                        return new SetExtraDataResult(false,
                            Error: $"Cannot access XDAT cell {cn} for Variable mark on surface {surfaceNumber}.");
                    if (pathUsed == ExtraDataHelper.AccessPath.Unknown) pathUsed = path;

                    try { xcell.MakeSolveVariable(); }
                    catch (Exception ex)
                    {
                        return new SetExtraDataResult(false,
                            Error: $"Failed to mark cell {cn} as Variable: {ex.Message}");
                    }
                    touched.Add(cn);
                }

                var entries = new List<ExtraDataEntry>();
                foreach (var cn in touched)
                {
                    var xcell = ExtraDataHelper.TryGetZernikeCell(surface, cn, out var rbPath);
                    if (xcell == null && rbPath != ExtraDataHelper.AccessPath.ZernikeTypedInterface)
                        xcell = ExtraDataHelper.TryGetCell(surface, cn, out _);
                    if (xcell == null) continue;
                    entries.Add(new ExtraDataEntry(cn, ExtraDataHelper.ReadValue(xcell), ExtraDataHelper.IsVariable(xcell)));
                }

                return new SetExtraDataResult(
                    Success: true,
                    SurfaceNumber: surfaceNumber,
                    SurfaceType: surfType,
                    AccessPath: pathUsed.ToString(),
                    Entries: entries.ToArray());
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SetExtraDataResult(false, Error: ex.Message);
        }
    }
}
