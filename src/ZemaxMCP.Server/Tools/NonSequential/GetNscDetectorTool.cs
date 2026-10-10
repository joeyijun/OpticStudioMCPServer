using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI;
using ZOSAPI.Editors.NCE;

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
        string? FluxUnit = null,
        double? PixelPitchX = null,
        double? PixelPitchY = null,
        double? PixelArea = null,
        string? PositionUnit = null,
        string? PixelOrientation = null,
        double? RoiFluxSum = null,
        double? RoiFluxIntegral = null,
        double? RoiFractionOfDetectorFlux = null,
        double? LaunchedFlux = null,
        double? RoiFractionOfLaunchedFlux = null,
        double? TotalDetectorFractionOfLaunchedFlux = null,
        double? MissedRayCount = null,
        string? NormalizationCaveat = null,
        double? RoiPixelMin = null,
        double? RoiPixelMax = null,
        double? RoiPixelMean = null,
        int? RoiNonzeroPixelCount = null,
        int? RoiPeakRow = null,
        int? RoiPeakColumn = null,
        NscDetectorTilePreview.Plan? NativeTilePlan = null,
        double[][]? RoiMeanHeatmap = null,
        string? RoiMeanHeatmapCaveat = null);

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
        [Description("Optional positive launched source flux from the SAME ray trace in the detector's native flux units; used only to calculate explicitly normalized ratios.")] double? launchedFlux = null,
        [Description("Return up to 64 tiles per page (each <=4096 native pixels) to reconstruct a large detector with multiple bounded ROI calls.")] bool includeTilePlan = false,
        [Description("0-based page of the native row-major tile plan.")] int tilePlanPage = 0,
        [Description("Optional mean-binned heatmap size 2..16, only with includePixels=true. 0 disables heatmap.")] int heatmapBins = 0,
        CancellationToken cancellationToken = default)
    {
        if (objectNumber < 1)
            return new Result(false, "objectNumber must be at least 1.", objectNumber, null, null, 0, 0, 0, null);
        if (tilePlanPage < 0 || heatmapBins != 0 && (heatmapBins < 2 || heatmapBins > 16) ||
            heatmapBins > 0 && !includePixels)
            return new Result(false, "tilePlanPage must be nonnegative and heatmapBins is 0 or 2..16 with includePixels=true.",
                objectNumber, null, null, 0, 0, 0, null);
        if (dataType is not (0 or 1) || startRow < 0 || startColumn < 0 || rowCount < 0 || columnCount < 0 ||
            (launchedFlux.HasValue && (launchedFlux.Value <= 0 || double.IsNaN(launchedFlux.Value) || double.IsInfinity(launchedFlux.Value))))
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

                NscDetectorTilePreview.Plan? tilePlan = null;
                if (includeTilePlan)
                    tilePlan = NscDetectorTilePreview.MakePlan(rows, columns, tilePlanPage);

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
                if (row.Type is not (ZOSAPI.Editors.NCE.ObjectType.DetectorColor or
                    ZOSAPI.Editors.NCE.ObjectType.DetectorPolar))
                {
                    if (nce.GetDetectorData(objectNumber, 0, 0, out var flux) &&
                        !double.IsNaN(flux) && !double.IsInfinity(flux))
                        totalFlux = flux;
                    if (nce.GetDetectorData(objectNumber, -3, 0, out var hits) &&
                        !double.IsNaN(hits) && !double.IsInfinity(hits))
                        rayHits = hits;
                }
                if (includePixels && (!totalFlux.HasValue || !rayHits.HasValue))
                    return new Result(false, "Trace the NSC system before requesting pixel data; flux/hit statistics are unavailable.",
                        objectNumber, row.TypeName, row.Comment, columns, rows,
                        totalPixels, row.TypeData.DetectorShowAs.ToString());

                // Only a rectangular detector has a simple, defensible
                // constant pixel area and orientation. Other detector classes
                // retain an explicit "unknown" scale, not a guessed spacing.
                double? pitchX = null, pitchY = null, pixelArea = null;
                string? orientation = null;
                if (row.Type == ObjectType.DetectorRectangle &&
                    row.ObjectData is IObjectDetectorRectangle rect)
                {
                    var nx = rect.NumberXPixels;
                    var ny = rect.NumberYPixels;
                    if (nx > 0 && ny > 0 && (ulong)nx * (ulong)ny == totalPixels &&
                        rect.XHalfWidth > 0 && rect.YHalfWidth > 0 &&
                        !double.IsNaN(rect.XHalfWidth) && !double.IsInfinity(rect.XHalfWidth) &&
                        !double.IsNaN(rect.YHalfWidth) && !double.IsInfinity(rect.YHalfWidth))
                    {
                        // GetDetectorDimensions is not consistently documented
                        // as X/Y vs row/column order between ZOS releases.
                        // Geometry and linear index follow the explicit detector
                        // object (#X pixels, #Y pixels), not an inferred swap.
                        columns = (uint)nx;
                        rows = (uint)ny;
                        pitchX = 2d * rect.XHalfWidth / nx;
                        pitchY = 2d * rect.YHalfWidth / ny;
                        pixelArea = pitchX * pitchY;
                        orientation = "native lower-left (-X,-Y); +column is +X, +row is +Y; not screen-image orientation";
                    }
                }

                double[][]? pixelGrid = null;
                double? roiSum = null, roiIntegral = null, roiDetectorFraction = null,
                    roiLaunchedFraction = null;
                double? pixelMin = null, pixelMax = null, pixelMean = null;
                int? nonzeroCount = null, peakRow = null, peakColumn = null;
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
                    roiSum = pixelGrid.Sum(line => line.Sum());
                    if (double.IsNaN(roiSum.Value) || double.IsInfinity(roiSum.Value))
                        throw new InvalidOperationException("The ROI pixel sum overflows the native detector data range.");
                    var pixels = pixelGrid.SelectMany(line => line).ToArray();
                    pixelMin = pixels.Min();
                    pixelMax = pixels.Max();
                    pixelMean = roiSum / pixels.Length;
                    nonzeroCount = pixels.Count(x => x != 0);
                    var peakIndex = Array.IndexOf(pixels, pixelMax.Value);
                    peakRow = startRow + peakIndex / (int)width;
                    peakColumn = startColumn + peakIndex % (int)width;
                    // Pixel extrema and their position are native quantities,
                    // not total power or throughput. Use ROI integration below.
                    // Flux-per-area integrates to flux only if a real, uniform
                    // physical pixel area is known; volume dataType=1 is
                    // absorbed flux and is NOT area-weighted.
                    roiIntegral = dataType == 0 ||
                        row.Type == ObjectType.DetectorVolume ? roiSum :
                        pixelArea.HasValue ? roiSum * pixelArea.Value : null;
                    if (roiIntegral.HasValue &&
                        (double.IsNaN(roiIntegral.Value) || double.IsInfinity(roiIntegral.Value)))
                        throw new InvalidOperationException("The ROI flux integral is non-finite.");
                    if (roiIntegral.HasValue && totalFlux.HasValue && totalFlux.Value > 0)
                        roiDetectorFraction = roiIntegral.Value / totalFlux.Value;
                    if (roiIntegral.HasValue && launchedFlux.HasValue)
                        roiLaunchedFraction = roiIntegral.Value / launchedFlux.Value;
                    if ((roiDetectorFraction.HasValue && (double.IsNaN(roiDetectorFraction.Value) || double.IsInfinity(roiDetectorFraction.Value))) ||
                        (roiLaunchedFraction.HasValue && (double.IsNaN(roiLaunchedFraction.Value) || double.IsInfinity(roiLaunchedFraction.Value))))
                        throw new InvalidOperationException("ROI normalization is non-finite; verify source and detector flux scales.");
                }

                double? detectorLaunchedFraction = launchedFlux.HasValue && totalFlux.HasValue
                    ? totalFlux.Value / launchedFlux.Value : null;
                if (detectorLaunchedFraction.HasValue &&
                    (double.IsNaN(detectorLaunchedFraction.Value) || double.IsInfinity(detectorLaunchedFraction.Value)))
                    throw new InvalidOperationException("Detector/source normalization is non-finite; verify the launched-flux unit and magnitude.");

                return new Result(
                    true, null, objectNumber, row.TypeName, row.Comment,
                    columns, rows, totalPixels, row.TypeData.DetectorShowAs.ToString(),
                    totalFlux, rayHits,
                    includePixels ? (dataType == 0 ? "incident-flux" :
                        row.Type == ObjectType.DetectorVolume ? "absorbed-flux" : "flux-per-area") : null,
                    startRow, startColumn, pixelGrid, "OpticStudio native NSC source-flux units",
                    pitchX, pitchY, pixelArea, pitchX.HasValue ? "lens units" : null,
                    orientation, roiSum, roiIntegral, roiDetectorFraction,
                    launchedFlux, roiLaunchedFraction,
                    detectorLaunchedFraction,
                    null,
                    "Detector hit counts may include repeated or split ray hits; total rays that missed the detector cannot be inferred from them. " +
                    "ROI pixel summation is incoherent. source normalization requires identical units, sources and trace. " +
                    "For non-rectangular detectors the exact pixel physical area and ROI flux-integral may be unavailable. " +
                    "DetectorVolume dataType=1 integrates ABSORBED flux: its launched-flux ratio is absorption, not collection throughput. " +
                    "ROI pixel maxima are not integrated detector power.",
                    pixelMin, pixelMax, pixelMean, nonzeroCount, peakRow, peakColumn,
                    tilePlan,
                    pixelGrid != null && heatmapBins > 0
                        ? NscDetectorTilePreview.MeanHeatmap(pixelGrid, heatmapBins) : null,
                    heatmapBins > 0 ? "Native ROI ordering, per-bin arithmetic MEAN of pixel values; not a detector-power integral, RGB visualization, or physical orientation calibration." : null);
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
