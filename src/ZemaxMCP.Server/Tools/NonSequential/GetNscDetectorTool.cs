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
        string? DisplayMode,
        double? TotalIncidentFlux = null,
        double? RayHits = null,
        string? PixelDataKind = null,
        int RoiStartRow = 0,
        int RoiStartColumn = 0,
        double[][]? RoiPixels = null,
        string? FluxUnit = null);

    [ZemaxTool(Name = "zemax_get_nsc_detector")]
    [Description("Read NSC detector dimensions, total incident flux, ray hits and optionally a bounded ROI flux/irradiance pixel matrix. Pixel values are native OpticStudio data (not automatically power-normalized). Use after tracing and verify source-power units.")]
    public async Task<Result> ExecuteAsync(
        [Description("NSC detector object number (1-indexed)")] int objectNumber,
        [Description("Read a pixel ROI as a 2D matrix (up to 4096 pixels). False returns detector summary only.")] bool includePixels = false,
        [Description("Native NSC detector data: 0 = incident flux, 1 = flux/area for surface/rectangle detectors or absorbed flux for detector volumes.")] int dataType = 0,
        [Description("ROI start row, 0-based in detector's native row ordering.")] int startRow = 0,
        [Description("ROI start column, 0-based in detector's native column ordering.")] int startColumn = 0,
        [Description("ROI height; 0 = remaining detector rows.")] int rowCount = 0,
        [Description("ROI width; 0 = remaining detector columns.")] int columnCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (objectNumber < 1)
            return new Result(false, "objectNumber must be at least 1.", objectNumber, null, null, 0, 0, 0, null);
        if (dataType is not (0 or 1) || startRow < 0 || startColumn < 0 || rowCount < 0 || columnCount < 0)
            return new Result(false, "dataType must be 0/1 and ROI coordinates/sizes cannot be negative.", objectNumber, null, null, 0, 0, 0, null);

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
                if (!NscObjectClassification.IsDetector(row))
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

                if (includePixels && (row.Type is ZOSAPI.Editors.NCE.ObjectType.DetectorColor or
                    ZOSAPI.Editors.NCE.ObjectType.DetectorPolar))
                    return new Result(false, "Color/polar detectors require their dedicated NSC detector-data API; generic flux pixels are not interpreted as irradiance.",
                        objectNumber, row.TypeName, row.Comment, columns, rows, totalPixels, row.TypeData.DetectorShowAs.ToString());

                // The official GetDetectorData pixel index starts at one.
                // Index zero is a summary statistic, never pixel (0,0).
                // A newly opened lens may have no traced detector buffer.
                // Preserve the pre-1.6 dimensional readback behavior: missing
                // power statistics are explicitly null, not a fabricated zero
                // and not an error for a dimensions-only request.
                double? totalFlux = null;
                double? rayHits = null;
                if (nce.GetDetectorData(objectNumber, 0, 0, out var flux) &&
                    !double.IsNaN(flux) && !double.IsInfinity(flux))
                    totalFlux = flux;
                if (nce.GetDetectorData(objectNumber, -3, 0, out var hits) &&
                    !double.IsNaN(hits) && !double.IsInfinity(hits))
                    rayHits = hits;
                if (includePixels && (!totalFlux.HasValue || !rayHits.HasValue))
                    return new Result(false, "Trace the NSC system before requesting pixel data; flux/hit statistics are unavailable.",
                        objectNumber, row.TypeName, row.Comment, columns, rows,
                        totalPixels, row.TypeData.DetectorShowAs.ToString());

                double[][]? pixelGrid = null;
                if (includePixels)
                {
                    var height = rowCount == 0 ? (long)rows - startRow : rowCount;
                    var width = columnCount == 0 ? (long)columns - startColumn : columnCount;
                    if (startRow >= rows || startColumn >= columns ||
                        height < 1 || width < 1 ||
                        startRow + height > rows || startColumn + width > columns ||
                        height * width > 4096)
                        return new Result(false, "ROI must lie inside the detector and contain 1..4096 pixels. Request smaller tiles for a large detector.",
                            objectNumber, row.TypeName, row.Comment, columns, rows, totalPixels,
                            row.TypeData.DetectorShowAs.ToString(), totalFlux, rayHits);
                    pixelGrid = new double[(int)height][];
                    for (var y = 0; y < (int)height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        pixelGrid[y] = new double[(int)width];
                        for (var x = 0; x < (int)width; x++)
                        {
                            var pixel = checked((int)(((long)startRow + y) * columns + startColumn + x + 1));
                            if (!nce.GetDetectorData(objectNumber, pixel, dataType, out var value) ||
                                double.IsNaN(value) || double.IsInfinity(value))
                                throw new InvalidOperationException($"Detector pixel {pixel} is unavailable or non-finite.");
                            pixelGrid[y][x] = value;
                        }
                    }
                }

                return new Result(
                    true, null, objectNumber, row.TypeName, row.Comment,
                    columns, rows, totalPixels, row.TypeData.DetectorShowAs.ToString(),
                    totalFlux, rayHits,
                    includePixels ? (dataType == 0 ? "incident-flux" :
                        row.Type == ZOSAPI.Editors.NCE.ObjectType.DetectorVolume ? "absorbed-flux" : "flux-per-area") : null,
                    startRow, startColumn, pixelGrid, "OpticStudio native NSC source-flux units");
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
