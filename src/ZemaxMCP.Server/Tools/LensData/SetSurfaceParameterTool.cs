using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Editors.LDE;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SetSurfaceParameterTool
{
    private readonly IZemaxSession _session;

    public SetSurfaceParameterTool(IZemaxSession session) => _session = session;

    public record ParameterEntry(int Number, double Value);

    public record SetParameterResult(
        bool Success,
        string? Error = null,
        int SurfaceNumber = 0,
        string? SurfaceType = null,
        ParameterEntry[]? Parameters = null);

    [ZemaxTool(Name = "zemax_set_surface_parameter")]
    [Description(
        "Get or set surface-type-specific parameters (PARM values). "
        + "For CoordinateBreak: PARM 1=Decenter X (mm), 2=Decenter Y (mm), "
        + "3=Tilt About X (deg), 4=Tilt About Y (deg), 5=Tilt About Z (deg), "
        + "6=Order (0=decenter-then-tilt, 1=tilt-then-decenter). "
        + "Read mode: omit value and batchSet to return current values. "
        + "Single set: provide parameterNumber + value. "
        + "Batch set: use batchSet string like '3:0.2,4:0.1,6:1'.")]
    public async Task<SetParameterResult> ExecuteAsync(
        [Description("Surface number")] int surfaceNumber,
        [Description("Parameter number (1-indexed). 0 to return all parameters.")] int parameterNumber = 0,
        [Description("Value to set (omit to read only)")] double? value = null,
        [Description("Make this parameter variable for optimization")] bool? makeVariable = null,
        [Description("Batch set: comma-separated 'num:value' pairs, e.g. '3:0.2,4:0.1,6:1'")] string? batchSet = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (parameterNumber < 0 || parameterNumber > 20)
                return new SetParameterResult(false,
                    Error: $"Invalid parameter number: {parameterNumber}. Valid range: 1-20 (0 for all).");
            if (value.HasValue && (double.IsNaN(value.Value) || double.IsInfinity(value.Value)))
                return new SetParameterResult(false, Error: "Parameter value must be finite.");
            if (parameterNumber == 0 && (value.HasValue || makeVariable == true))
                return new SetParameterResult(false, Error: "A parameterNumber from 1 to 20 is required when value or makeVariable is supplied.");

            var parsedBatch = new List<ParameterEntry>();
            if (!string.IsNullOrWhiteSpace(batchSet))
            {
                foreach (var pair in batchSet.Split(','))
                {
                    var parts = pair.Trim().Split(':');
                    if (parts.Length != 2 ||
                        !int.TryParse(parts[0].Trim(), out var pNum) || pNum < 1 || pNum > 20 ||
                        !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pVal) ||
                        double.IsNaN(pVal) || double.IsInfinity(pVal))
                    {
                        return new SetParameterResult(false,
                            Error: $"Invalid batch entry '{pair}'. Expected parameter 1-20 and a finite value, e.g. '3:0.2'.");
                    }
                    parsedBatch.Add(new ParameterEntry(pNum, pVal));
                }
            }

            var isMutation = parsedBatch.Count > 0 || (parameterNumber > 0 && value.HasValue) || (parameterNumber > 0 && makeVariable == true);
            var command = isMutation ? "SetSurfaceParameter" : "GetSurfaceParameter";
            var parameters = new Dictionary<string, object?>
            {
                ["surfaceNumber"] = surfaceNumber,
                ["parameterNumber"] = parameterNumber,
                ["value"] = value,
                ["batchSet"] = batchSet
            };

            return await _session.ExecuteAsync(command, parameters, system =>
            {
                var lde = system.LDE;
                if (surfaceNumber < 0 || surfaceNumber >= lde.NumberOfSurfaces)
                    throw new ArgumentException($"Invalid surface number: {surfaceNumber}. Valid range: 0-{lde.NumberOfSurfaces - 1}");

                var surface = lde.GetSurfaceAt(surfaceNumber);
                var surfType = surface.Type.ToString();

                if (parsedBatch.Count > 0)
                {
                    var entries = new List<ParameterEntry>();
                    foreach (var requested in parsedBatch)
                    {
                        var bCell = surface.GetSurfaceCell(SurfaceColumn.Par0 + requested.Number);
                        WriteParameterValue(bCell, requested.Value);
                        entries.Add(new ParameterEntry(requested.Number, ReadParameterValue(bCell)));
                    }
                    return new SetParameterResult(true, SurfaceNumber: surfaceNumber, SurfaceType: surfType, Parameters: entries.ToArray());
                }

                if (parameterNumber > 0 && value.HasValue)
                {
                    var cell = surface.GetSurfaceCell(SurfaceColumn.Par0 + parameterNumber);
                    WriteParameterValue(cell, value.Value);
                    if (makeVariable == true) cell.MakeSolveVariable();
                    return new SetParameterResult(true, SurfaceNumber: surfaceNumber, SurfaceType: surfType,
                        Parameters: new[] { new ParameterEntry(parameterNumber, ReadParameterValue(cell)) });
                }

                if (parameterNumber > 0 && makeVariable == true)
                {
                    var cell = surface.GetSurfaceCell(SurfaceColumn.Par0 + parameterNumber);
                    cell.MakeSolveVariable();
                    return new SetParameterResult(true, SurfaceNumber: surfaceNumber, SurfaceType: surfType,
                        Parameters: new[] { new ParameterEntry(parameterNumber, ReadParameterValue(cell)) });
                }

                var readEntries = new List<ParameterEntry>();
                var consecutiveFailures = 0;
                for (var p = 1; p <= 20; p++)
                {
                    try
                    {
                        var cell = surface.GetSurfaceCell(SurfaceColumn.Par0 + p);
                        if (cell == null) { consecutiveFailures++; continue; }
                        var readback = ReadParameterValue(cell);
                        consecutiveFailures = 0;
                        if (parameterNumber == 0 || parameterNumber == p)
                            readEntries.Add(new ParameterEntry(p, readback));
                    }
                    catch
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= 3) break;
                    }
                }

                return new SetParameterResult(true, SurfaceNumber: surfaceNumber, SurfaceType: surfType, Parameters: readEntries.ToArray());
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SetParameterResult(false, Error: ex.Message);
        }
    }

    private static void WriteParameterValue(dynamic cell, double value)
    {
        try { cell.DoubleValue = value; }
        catch { cell.IntegerValue = checked((int)value); }
    }

    private static double ReadParameterValue(dynamic cell)
    {
        try { return cell.DoubleValue; }
        catch { return cell.IntegerValue; }
    }
}
