using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.NonSequential;

[ZemaxToolType]
public sealed class GetNscDetectorTool
{
    private readonly IZemaxSession _session;

    public GetNscDetectorTool(IZemaxSession session) => _session = session;

    public record Result(
        bool Success,
        string? Error,
        int ObjectNumber,
        string? ObjectType,
        string? Comment,
        uint PixelColumns,
        uint PixelRows,
        uint TotalPixels,
        string? DisplayMode);

    [ZemaxTool(Name = "zemax_get_nsc_detector")]
    [Description("Inspect an NSC detector's pixel dimensions and display mode without reading or changing detector data. Requires a non-sequential system.")]
    public async Task<Result> ExecuteAsync(
        [Description("NSC detector object number (1-indexed)")] int objectNumber,
        CancellationToken cancellationToken = default)
    {
        if (objectNumber < 1)
            return new Result(false, "objectNumber must be at least 1.", objectNumber, null, null, 0, 0, 0, null);

        try
        {
            return await _session.ExecuteAsync("GetNscDetector", new Dictionary<string, object?>
            {
                ["objectNumber"] = objectNumber
            }, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (system.Mode != SystemType.NonSequential)
                    return new Result(false, "The current system is sequential. Open or create a non-sequential system before using this tool.", objectNumber, null, null, 0, 0, 0, null);

                var nce = system.NCE ?? throw new InvalidOperationException("Non-Sequential Component Editor is not available.");
                if (objectNumber > nce.NumberOfObjects)
                    return new Result(false, $"Object {objectNumber} does not exist; the system has {nce.NumberOfObjects} NSC objects.", objectNumber, null, null, 0, 0, 0, null);

                var row = nce.GetObjectAt(objectNumber)
                    ?? throw new InvalidOperationException($"OpticStudio returned no NCE row for object {objectNumber}.");
                if (!row.TypeData.ObjectIsADetector)
                    return new Result(false, $"Object {objectNumber} ({row.TypeName}) is not a detector.", objectNumber, row.TypeName, row.Comment, 0, 0, 0, null);

                // ZOS-API 2026 R1 signature is GetDetectorDimensions(ObjectNumber, out Rows, out Cols).
                var dimensionsAvailable = nce.GetDetectorDimensions(objectNumber, out var rows, out var columns);
                if (!dimensionsAvailable)
                    return new Result(false, $"OpticStudio could not read dimensions for detector object {objectNumber}.", objectNumber, row.TypeName, row.Comment, 0, 0, 0, row.TypeData.DetectorShowAs.ToString());
                if (rows == 0 || columns == 0)
                    return new Result(false, $"Detector object {objectNumber} returned invalid zero dimensions {columns}x{rows}.", objectNumber, row.TypeName, row.Comment, columns, rows, 0, row.TypeData.DetectorShowAs.ToString());

                cancellationToken.ThrowIfCancellationRequested();
                var totalPixels = nce.GetDetectorSize(objectNumber);
                var expectedPixels = checked((ulong)rows * columns);
                if (totalPixels == 0 || (ulong)totalPixels != expectedPixels)
                {
                    return new Result(false,
                        $"Detector object {objectNumber} dimension/size mismatch: {columns} columns x {rows} rows = {expectedPixels} pixels, but GetDetectorSize returned {totalPixels}.",
                        objectNumber, row.TypeName, row.Comment, columns, rows, totalPixels, row.TypeData.DetectorShowAs.ToString());
                }

                return new Result(
                    true,
                    null,
                    objectNumber,
                    row.TypeName,
                    row.Comment,
                    columns,
                    rows,
                    totalPixels,
                    row.TypeData.DetectorShowAs.ToString());
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, objectNumber, null, null, 0, 0, 0, null);
        }
    }
}
