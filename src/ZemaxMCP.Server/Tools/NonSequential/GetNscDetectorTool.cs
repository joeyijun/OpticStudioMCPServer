using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.NonSequential;

[McpServerToolType]
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

    [McpServerTool(Name = "zemax_get_nsc_detector")]
    [Description("Inspect an NSC detector's dimensions and display mode without reading or changing detector data. Requires a non-sequential system.")]
    public async Task<Result> ExecuteAsync(
        [Description("NSC detector object number (1-indexed)")] int objectNumber)
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
                if (system.Mode != SystemType.NonSequential)
                    return new Result(false, "The current system is sequential. Open or create a non-sequential system before using this tool.", objectNumber, null, null, 0, 0, 0, null);

                var nce = system.NCE;
                if (objectNumber > nce.NumberOfObjects)
                    return new Result(false, $"Object {objectNumber} does not exist; the system has {nce.NumberOfObjects} NSC objects.", objectNumber, null, null, 0, 0, 0, null);

                var row = nce.GetObjectAt(objectNumber);
                if (!row.TypeData.ObjectIsADetector)
                    return new Result(false, $"Object {objectNumber} ({row.TypeName}) is not a detector.", objectNumber, row.TypeName, row.Comment, 0, 0, 0, null);

                var dimensionsAvailable = nce.GetDetectorDimensions(objectNumber, out var columns, out var rows);
                var totalPixels = dimensionsAvailable ? nce.GetDetectorSize(objectNumber) : 0;
                return new Result(
                    dimensionsAvailable,
                    dimensionsAvailable ? null : $"OpticStudio could not read dimensions for detector object {objectNumber}.",
                    objectNumber,
                    row.TypeName,
                    row.Comment,
                    columns,
                    rows,
                    totalPixels,
                    row.TypeData.DetectorShowAs.ToString());
            });
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, objectNumber, null, null, 0, 0, 0, null);
        }
    }
}
