using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;
using ZOSAPI.Analysis;
using ZOSAPI.Analysis.Data;
using ZOSAPI.Analysis.PhysicalOptics;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class PopTool
{
    private readonly IZemaxSession _session;
    private readonly McpJobManager _jobs;

    public PopTool(IZemaxSession session, McpJobManager jobs)
    {
        _session = session;
        _jobs = jobs;
    }

    public record PopResult(
        bool Success,
        string? Error = null,
        string? BeamType = null,
        string? DataType = null,
        double PeakIrradiance = 0,
        double TotalPower = 0,
        double GridWidthX = 0,
        double GridWidthY = 0,
        double PixelPitchX = 0,
        double PixelPitchY = 0,
        int Nx = 0,
        int Ny = 0,
        int StartSurfaceResolved = 0,
        int EndSurfaceResolved = 0,
        int WavelengthResolved = 0,
        int FieldResolved = 0,
        double SurfaceToBeamApplied = 0,
        bool ResampleAfterRefractionApplied = false,
        double[][]? Grid = null,
        string? GridFilePath = null,
        string? BmpFilePath = null,
        string? OutputBeamFilePath = null,
        string? JobId = null,
        string? JobState = null,
        bool PowerMetricsApplicable = false);

    private const int InlineGridCellLimit = 65536;

    [ZemaxTool(Name = "zemax_pop")]
    [Description(
        "Run Physical Optics Propagation and return the intensity/phase/transfer grid. " +
        "POP can optionally export raw-grid, BMP, and ZBF files and can temporarily enable per-surface ResampleAfterRefraction; " +
        "therefore it is classified HighImpact. Temporary surface resampling is restored after the analysis. " +
        "startSurface/endSurface control the propagation range (0 keeps the POP default; endSurface=-1 selects the image surface). " +
        "surfaceToBeam is the input-side axial beam offset, not image-plane defocus. " +
        "autoSampling/autoWidth override autoCalculate per category when non-null. " +
        "beamParams is a comma-separated list applied to the active beam type's published parameter slots. " +
        "Grid <= 256x256 returns inline unless outputGridPath is supplied; larger grids require outputGridPath. " +
        "File outputs do not overwrite existing files unless overwriteOutputFiles=true. All linear units are lens units.")]
    public async Task<PopResult> ExecuteAsync(
        [Description("Beam type: GaussianWaist, GaussianAngle, GaussianSizeAngle, TopHat, File, DLL, Multimode, AstigmaticGaussian")] string beamType = "GaussianWaist",
        [Description("Comma-separated beam parameters in the active beam type's published order. Leave empty for defaults.")] string? beamParams = null,
        [Description("POP start surface (1-indexed); 0 keeps the POP default")] int startSurface = 0,
        [Description("POP end surface (1-indexed); -1 uses image surface, 0 keeps the POP default")] int endSurface = -1,
        [Description("X sampling: 1=32, 2=64, 3=128, 4=256, 5=512, 6=1024")] int xSampling = 5,
        [Description("Y sampling: 1=32, 2=64, 3=128, 4=256, 5=512, 6=1024")] int ySampling = 5,
        [Description("X width in lens units; 0 leaves the POP default")] double xWidth = 0,
        [Description("Y width in lens units; 0 leaves the POP default")] double yWidth = 0,
        [Description("Call AutoCalculateBeamSampling after explicit sampling/width values")] bool autoCalculate = true,
        [Description("Data type: Irradiance, EXIrradiance, EYIrradiance, Phase, EXPhase, EYPhase, TransferMagnitude, TransferPhase")] string dataType = "Irradiance",
        [Description("Use peak-irradiance normalization")] bool peakNormalize = false,
        [Description("Input-side axial beam offset from startSurface; must be finite")] double surfaceToBeam = 0,
        [Description("Optional raw-grid output path")] string? outputGridPath = null,
        [Description("Optional .BMP output path")] string? exportBmpPath = null,
        [Description("Wavelength number (1-indexed); 0 keeps the POP default")] int wavelength = 0,
        [Description("Field number (1-indexed); 0 keeps the POP default")] int field = 0,
        [Description("Sampling auto-calc override; null inherits autoCalculate")] bool? autoSampling = null,
        [Description("Width auto-calc override; null inherits autoCalculate")] bool? autoWidth = null,
        [Description("Temporarily force ResampleAfterRefraction=true on the selected LDE surface range; original values are restored afterward")] bool resampleAfterRefraction = false,
        [Description("If true, set POP UsePolarization=false")] bool ignorePolarization = false,
        [Description("Optional .ZBF output path")] string? outputBeamFilePath = null,
        [Description("Queue POP and return a job id immediately")] bool runInBackground = true,
        [Description("Allow replacement of requested raw-grid/BMP/ZBF output files")] bool overwriteOutputFiles = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateStaticInputs(beamType, beamParams, startSurface, endSurface, xSampling, ySampling,
                xWidth, yWidth, dataType, surfaceToBeam, outputGridPath, exportBmpPath,
                wavelength, field, outputBeamFilePath, overwriteOutputFiles);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PopResult(false, Error: ex.Message);
        }

        if (runInBackground)
        {
            var job = _jobs.Enqueue("zemax_pop", async context =>
            {
                context.ReportProgress(0, "Waiting for the ZOS-API job slot.");
                context.CancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteAsync(
                    beamType, beamParams, startSurface, endSurface, xSampling, ySampling, xWidth, yWidth,
                    autoCalculate, dataType, peakNormalize, surfaceToBeam, outputGridPath, exportBmpPath,
                    wavelength, field, autoSampling, autoWidth, resampleAfterRefraction, ignorePolarization,
                    outputBeamFilePath, runInBackground: false, overwriteOutputFiles: overwriteOutputFiles,
                    cancellationToken: context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
                if (!result.Success)
                    throw new InvalidOperationException(result.Error ?? "POP analysis failed.");
                context.SetResult(result);
                context.ReportProgress(1, "Completed.");
            });
            return new PopResult(true, JobId: job.JobId, JobState: job.State.ToString());
        }

        try
        {
            var parsedBeamParameters = ParseBeamParameters(beamParams);
            var parameters = new Dictionary<string, object?>
            {
                ["beamType"] = beamType,
                ["beamParams"] = beamParams,
                ["startSurface"] = startSurface,
                ["endSurface"] = endSurface,
                ["xSampling"] = xSampling,
                ["ySampling"] = ySampling,
                ["xWidth"] = xWidth,
                ["yWidth"] = yWidth,
                ["autoCalculate"] = autoCalculate,
                ["dataType"] = dataType,
                ["peakNormalize"] = peakNormalize,
                ["surfaceToBeam"] = surfaceToBeam,
                ["outputGridPath"] = outputGridPath,
                ["exportBmpPath"] = exportBmpPath,
                ["wavelength"] = wavelength,
                ["field"] = field,
                ["autoSampling"] = autoSampling,
                ["autoWidth"] = autoWidth,
                ["resampleAfterRefraction"] = resampleAfterRefraction,
                ["ignorePolarization"] = ignorePolarization,
                ["outputBeamFilePath"] = outputBeamFilePath,
                ["overwriteOutputFiles"] = overwriteOutputFiles
            };

            return await _session.ExecuteAsync("Pop", parameters, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateSystemInputs(system, startSurface, endSurface, wavelength, field);

                var finalGridPath = NormalizeOutputPath(outputGridPath, null, overwriteOutputFiles);
                var finalBmpPath = NormalizeOutputPath(exportBmpPath, ".BMP", overwriteOutputFiles);
                var finalZbfPath = NormalizeOutputPath(outputBeamFilePath, ".ZBF", overwriteOutputFiles);
                string? zbfTempPath = null;
                var resampleRestore = new List<(int Surface, bool Original)>();
                var analysis = system.Analyses.New_Analysis(AnalysisIDM.PhysicalOpticsPropagation)
                    ?? throw new InvalidOperationException("OpticStudio could not create a Physical Optics Propagation analysis.");

                try
                {
                    var settings = analysis.GetSettings() as IAS_PhysicalOpticsPropagation
                        ?? throw new InvalidOperationException("Failed to cast POP settings to IAS_PhysicalOpticsPropagation.");

                    if (!Enum.TryParse<POPBeamTypes>(beamType.Trim(), ignoreCase: true, out var bt) ||
                        !Enum.IsDefined(typeof(POPBeamTypes), bt))
                        throw new ArgumentException($"Invalid beamType '{beamType}'.");
                    if (!Enum.TryParse<POPDataTypes>(dataType.Trim(), ignoreCase: true, out var dt) ||
                        !Enum.IsDefined(typeof(POPDataTypes), dt))
                        throw new ArgumentException($"Invalid dataType '{dataType}'.");

                    settings.BeamType = bt;
                    settings.DataType = dt;

                    if (startSurface > 0)
                    {
                        settings.StartSurface.SetSurfaceNumber(startSurface);
                        if (settings.StartSurface.GetSurfaceNumber() != startSurface)
                            throw new InvalidOperationException($"OpticStudio did not apply POP startSurface={startSurface}.");
                    }

                    if (endSurface == -1)
                    {
                        settings.EndSurface.UseImageSurface();
                        var resolvedEnd = settings.EndSurface.GetSurfaceNumber();
                        if (resolvedEnd != 0 && resolvedEnd != system.LDE.NumberOfSurfaces - 1)
                            throw new InvalidOperationException("OpticStudio did not apply the POP image-surface endpoint.");
                    }
                    else if (endSurface > 0)
                    {
                        settings.EndSurface.SetSurfaceNumber(endSurface);
                        if (settings.EndSurface.GetSurfaceNumber() != endSurface)
                            throw new InvalidOperationException($"OpticStudio did not apply POP endSurface={endSurface}.");
                    }

                    if (wavelength > 0)
                    {
                        settings.Wavelength.SetWavelengthNumber(wavelength);
                        if (settings.Wavelength.GetWavelengthNumber() != wavelength)
                            throw new InvalidOperationException($"OpticStudio did not apply POP wavelength={wavelength}.");
                    }
                    if (field > 0)
                    {
                        settings.Field.SetFieldNumber(field);
                        if (settings.Field.GetFieldNumber() != field)
                            throw new InvalidOperationException($"OpticStudio did not apply POP field={field}.");
                    }

                    bool useAutoSampling = autoSampling ?? autoCalculate;
                    bool useAutoWidth = autoWidth ?? autoCalculate;
                    settings.XSampling = MapSampling(xSampling);
                    settings.YSampling = MapSampling(ySampling);
                    if (xWidth > 0) settings.XWidth = xWidth;
                    if (yWidth > 0) settings.YWidth = yWidth;

                    ApplyBeamParameters(settings, parsedBeamParameters);
                    settings.UsePeakIrradiance = peakNormalize;
                    settings.UsePolarization = !ignorePolarization;

                    if (useAutoSampling || useAutoWidth)
                    {
                        var savedXSampling = settings.XSampling;
                        var savedYSampling = settings.YSampling;
                        var savedXWidth = settings.XWidth;
                        var savedYWidth = settings.YWidth;
                        settings.AutoCalculateBeamSampling();
                        if (!useAutoSampling)
                        {
                            settings.XSampling = savedXSampling;
                            settings.YSampling = savedYSampling;
                        }
                        if (!useAutoWidth)
                        {
                            settings.XWidth = savedXWidth;
                            settings.YWidth = savedYWidth;
                        }
                    }

                    settings.SurfaceToBeam = surfaceToBeam;
                    if (!ApproximatelyEqual(settings.SurfaceToBeam, surfaceToBeam))
                        throw new InvalidOperationException($"OpticStudio did not preserve POP SurfaceToBeam={surfaceToBeam}.");

                    if (finalZbfPath != null)
                    {
                        zbfTempPath = CreateSiblingTempPath(finalZbfPath, ".ZBF");
                        settings.SaveOutputBeam = true;
                        settings.OutputBeamFile = zbfTempPath;
                    }
                    else
                    {
                        settings.SaveOutputBeam = false;
                    }

                    if (resampleAfterRefraction)
                        resampleRestore = ApplyTemporaryResampling(system, settings, startSurface);

                    cancellationToken.ThrowIfCancellationRequested();
                    ApplyAnalysisCancellable(analysis, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    var results = analysis.GetResults()
                        ?? throw new InvalidOperationException("POP analysis returned no results object.");
                    if (results.NumberOfDataGrids < 1)
                        throw new InvalidDataException("POP analysis produced no data grid. Check beam settings and surface configuration.");

                    var grid = results.GetDataGrid(0)
                        ?? throw new InvalidDataException("POP analysis returned a null first data grid.");
                    int nx = checked((int)grid.Nx);
                    int ny = checked((int)grid.Ny);
                    if (nx <= 0 || ny <= 0)
                        throw new InvalidDataException($"POP returned invalid grid dimensions {nx}x{ny}.");
                    double dx = grid.Dx;
                    double dy = grid.Dy;
                    if (!IsFinite(dx) || !IsFinite(dy) || dx == 0 || dy == 0)
                        throw new InvalidDataException($"POP returned invalid pixel pitch Dx={dx}, Dy={dy}.");

                    var matrix = grid.Values
                        ?? throw new InvalidDataException("POP data grid did not expose a Values matrix.");
                    if (matrix.GetLength(0) < nx || matrix.GetLength(1) < ny)
                        throw new InvalidDataException("POP Values matrix dimensions do not match Nx/Ny.");

                    var values2d = new double[ny][];
                    double peak = 0;
                    double total = 0;
                    double pixelArea = Math.Abs(dx * dy);
                    bool powerMetricsApplicable = dt == POPDataTypes.Irradiance ||
                                                  dt == POPDataTypes.EXIrradiance ||
                                                  dt == POPDataTypes.EYIrradiance;
                    for (int y = 0; y < ny; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        values2d[y] = new double[nx];
                        for (int x = 0; x < nx; x++)
                        {
                            var value = matrix[x, y];
                            if (!IsFinite(value))
                                throw new InvalidDataException($"POP grid contains a non-finite value at ({x},{y}).");
                            values2d[y][x] = value;
                            if (powerMetricsApplicable)
                            {
                                var magnitude = Math.Abs(value);
                                if (magnitude > peak) peak = magnitude;
                                total += value;
                            }
                        }
                    }
                    if (powerMetricsApplicable)
                        total *= pixelArea;

                    long cells = checked((long)nx * ny);
                    double[][]? inlineGrid = null;
                    string? gridFilePath = null;
                    if (finalGridPath == null)
                    {
                        if (cells > InlineGridCellLimit)
                            throw new InvalidOperationException($"Grid {nx}x{ny}={cells} exceeds inline limit {InlineGridCellLimit}. Provide outputGridPath.");
                        inlineGrid = values2d;
                    }
                    else
                    {
                        WriteGridBinAtomic(finalGridPath, nx, ny, dx, dy, values2d, overwriteOutputFiles, cancellationToken);
                        gridFilePath = finalGridPath;
                    }

                    string? bmpPath = null;
                    if (finalBmpPath != null)
                    {
                        var bmpTempPath = CreateSiblingTempPath(finalBmpPath, ".BMP");
                        try
                        {
                            if (!AnalysisBmpHelper.TryExportBmp(results, bmpTempPath, cancellationToken) || !File.Exists(bmpTempPath))
                                throw new IOException("POP BMP export was requested but OpticStudio did not produce a BMP file.");
                            cancellationToken.ThrowIfCancellationRequested();
                            CommitTempFile(bmpTempPath, finalBmpPath, overwriteOutputFiles);
                            bmpPath = finalBmpPath;
                        }
                        finally
                        {
                            if (File.Exists(bmpTempPath)) File.Delete(bmpTempPath);
                        }
                    }

                    string? zbfPath = null;
                    if (finalZbfPath != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (zbfTempPath == null || !File.Exists(zbfTempPath) || new FileInfo(zbfTempPath).Length == 0)
                            throw new IOException("POP ZBF export was requested but OpticStudio did not produce a non-empty output beam file.");
                        CommitTempFile(zbfTempPath, finalZbfPath, overwriteOutputFiles);
                        zbfTempPath = null;
                        zbfPath = finalZbfPath;
                    }

                    int startResolved = settings.StartSurface.GetSurfaceNumber();
                    int endResolved = settings.EndSurface.GetSurfaceNumber();
                    if (endResolved == 0)
                        endResolved = system.LDE.NumberOfSurfaces - 1;
                    int wlResolved = settings.Wavelength.GetWavelengthNumber();
                    int fldResolved = settings.Field.GetFieldNumber();

                    return new PopResult(
                        Success: true,
                        BeamType: bt.ToString(),
                        DataType: dt.ToString(),
                        PeakIrradiance: powerMetricsApplicable ? peak : 0,
                        TotalPower: powerMetricsApplicable ? total : 0,
                        GridWidthX: Math.Abs(dx) * nx,
                        GridWidthY: Math.Abs(dy) * ny,
                        PixelPitchX: dx,
                        PixelPitchY: dy,
                        Nx: nx,
                        Ny: ny,
                        StartSurfaceResolved: startResolved,
                        EndSurfaceResolved: endResolved,
                        WavelengthResolved: wlResolved,
                        FieldResolved: fldResolved,
                        SurfaceToBeamApplied: settings.SurfaceToBeam,
                        ResampleAfterRefractionApplied: resampleAfterRefraction,
                        Grid: inlineGrid,
                        GridFilePath: gridFilePath,
                        BmpFilePath: bmpPath,
                        OutputBeamFilePath: zbfPath,
                        PowerMetricsApplicable: powerMetricsApplicable);
                }
                finally
                {
                    try
                    {
                        RestoreTemporaryResampling(system, resampleRestore);
                    }
                    finally
                    {
                        if (zbfTempPath != null && File.Exists(zbfTempPath)) File.Delete(zbfTempPath);
                        try
                        {
                            if (analysis.IsRunning())
                            {
                                analysis.Terminate();
                                analysis.WaitForCompletion();
                            }
                        }
                        finally
                        {
                            analysis.Close();
                        }
                    }
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PopResult(false, Error: ex.Message);
        }
    }

    private static void ValidateStaticInputs(
        string beamType,
        string? beamParams,
        int startSurface,
        int endSurface,
        int xSampling,
        int ySampling,
        double xWidth,
        double yWidth,
        string dataType,
        double surfaceToBeam,
        string? outputGridPath,
        string? exportBmpPath,
        int wavelength,
        int field,
        string? outputBeamFilePath,
        bool overwriteOutputFiles)
    {
        if (string.IsNullOrWhiteSpace(beamType) ||
            !Enum.TryParse<POPBeamTypes>(beamType.Trim(), true, out var bt) ||
            !Enum.IsDefined(typeof(POPBeamTypes), bt))
            throw new ArgumentException($"Invalid beamType '{beamType}'.", nameof(beamType));
        if (string.IsNullOrWhiteSpace(dataType) ||
            !Enum.TryParse<POPDataTypes>(dataType.Trim(), true, out var dt) ||
            !Enum.IsDefined(typeof(POPDataTypes), dt))
            throw new ArgumentException($"Invalid dataType '{dataType}'.", nameof(dataType));
        if (startSurface < 0)
            throw new ArgumentOutOfRangeException(nameof(startSurface), "startSurface must be 0 or a positive surface number.");
        if (endSurface < -1)
            throw new ArgumentOutOfRangeException(nameof(endSurface), "endSurface must be -1, 0, or a positive surface number.");
        if (xSampling < 1 || xSampling > 6)
            throw new ArgumentOutOfRangeException(nameof(xSampling), "xSampling must be between 1 and 6.");
        if (ySampling < 1 || ySampling > 6)
            throw new ArgumentOutOfRangeException(nameof(ySampling), "ySampling must be between 1 and 6.");
        ValidateNonNegativeFinite(xWidth, nameof(xWidth));
        ValidateNonNegativeFinite(yWidth, nameof(yWidth));
        if (!IsFinite(surfaceToBeam))
            throw new ArgumentOutOfRangeException(nameof(surfaceToBeam), "surfaceToBeam must be finite.");
        if (wavelength < 0)
            throw new ArgumentOutOfRangeException(nameof(wavelength), "wavelength must be 0 or a positive wavelength number.");
        if (field < 0)
            throw new ArgumentOutOfRangeException(nameof(field), "field must be 0 or a positive field number.");

        _ = ParseBeamParameters(beamParams);
        _ = NormalizeOutputPath(outputGridPath, null, overwriteOutputFiles);
        _ = NormalizeOutputPath(exportBmpPath, ".BMP", overwriteOutputFiles);
        _ = NormalizeOutputPath(outputBeamFilePath, ".ZBF", overwriteOutputFiles);
    }

    private static void ValidateSystemInputs(ZOSAPI.IOpticalSystem system, int startSurface, int endSurface, int wavelength, int field)
    {
        var imageSurface = system.LDE.NumberOfSurfaces - 1;
        if (startSurface > imageSurface)
            throw new ArgumentOutOfRangeException(nameof(startSurface), $"startSurface must be 0 or between 1 and {imageSurface}.");
        if (endSurface > imageSurface)
            throw new ArgumentOutOfRangeException(nameof(endSurface), $"endSurface must be -1, 0, or between 1 and {imageSurface}.");
        if (startSurface > 0 && endSurface > 0 && startSurface > endSurface)
            throw new ArgumentException("startSurface cannot be greater than endSurface.");

        var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
        if (wavelength > wavelengthCount)
            throw new ArgumentOutOfRangeException(nameof(wavelength), $"wavelength must be 0 or between 1 and {wavelengthCount}.");
        var fieldCount = system.SystemData.Fields.NumberOfFields;
        if (field > fieldCount)
            throw new ArgumentOutOfRangeException(nameof(field), $"field must be 0 or between 1 and {fieldCount}.");
    }

    private static double[] ParseBeamParameters(string? beamParams)
    {
        if (string.IsNullOrWhiteSpace(beamParams))
            return Array.Empty<double>();

        var tokens = beamParams.Split(',');
        var values = new double[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!double.TryParse(tokens[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !IsFinite(value))
                throw new FormatException($"beamParams token '{tokens[i]}' at position {i + 1} is not a finite number.");
            values[i] = value;
        }
        return values;
    }

    private static void ApplyBeamParameters(IAS_PhysicalOpticsPropagation settings, double[] values)
    {
        if (values.Length == 0) return;
        if (values.Length > settings.NumberOfParameters)
            throw new ArgumentException($"The selected POP beam type exposes {settings.NumberOfParameters} parameters, but {values.Length} values were supplied.");

        int baseIndex;
        try
        {
            _ = settings.GetParameterName(0);
            baseIndex = 0;
        }
        catch
        {
            try
            {
                _ = settings.GetParameterName(1);
                baseIndex = 1;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to determine the POP beam-parameter index base from ZOS-API.", ex);
            }
        }

        for (int i = 0; i < values.Length; i++)
        {
            int index = baseIndex + i;
            settings.SetParameterValue(index, values[i]);
            var applied = settings.GetParameterValue(index);
            if (!IsFinite(applied))
                throw new InvalidOperationException($"POP beam parameter {i + 1} produced a non-finite readback.");
        }
    }

    private static List<(int Surface, bool Original)> ApplyTemporaryResampling(
        ZOSAPI.IOpticalSystem system,
        IAS_PhysicalOpticsPropagation settings,
        int startSurface)
    {
        var restore = new List<(int Surface, bool Original)>();
        try
        {
            int first = startSurface > 0 ? startSurface : 1;
            int last = settings.EndSurface.GetSurfaceNumber();
            if (last <= 0) last = system.LDE.NumberOfSurfaces - 1;
            for (int surface = first; surface <= last; surface++)
            {
                var row = system.LDE.GetSurfaceAt(surface)
                    ?? throw new InvalidOperationException($"Unable to access surface {surface} for POP resampling.");
                var data = row.PhysicalOpticsData
                    ?? throw new NotSupportedException($"Surface {surface} does not expose PhysicalOpticsData.");
                var original = data.ResampleAfterRefraction;
                restore.Add((surface, original));
                data.ResampleAfterRefraction = true;
                if (!data.ResampleAfterRefraction)
                    throw new InvalidOperationException($"OpticStudio did not apply ResampleAfterRefraction on surface {surface}.");
            }
            return restore;
        }
        catch
        {
            RestoreTemporaryResampling(system, restore);
            throw;
        }
    }

    private static void RestoreTemporaryResampling(ZOSAPI.IOpticalSystem system, IReadOnlyList<(int Surface, bool Original)> restore)
    {
        Exception? firstError = null;
        for (int i = restore.Count - 1; i >= 0; i--)
        {
            try
            {
                var item = restore[i];
                var row = system.LDE.GetSurfaceAt(item.Surface)
                    ?? throw new InvalidOperationException($"Unable to access surface {item.Surface} while restoring POP resampling.");
                row.PhysicalOpticsData.ResampleAfterRefraction = item.Original;
                if (row.PhysicalOpticsData.ResampleAfterRefraction != item.Original)
                    throw new InvalidOperationException($"Surface {item.Surface} POP resampling readback did not restore.");
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }

        if (firstError != null)
            throw new InvalidOperationException("Failed to restore one or more ResampleAfterRefraction values after POP. Use the pre-operation safety snapshot for recovery.", firstError);
    }

    private static void ApplyAnalysisCancellable(IA_ analysis, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        analysis.Apply();
        while (analysis.IsRunning())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                analysis.Terminate();
                analysis.WaitForCompletion();
                cancellationToken.ThrowIfCancellationRequested();
            }
            Thread.Sleep(50);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static SampleSizes MapSampling(int sampling) => sampling switch
    {
        1 => SampleSizes.S_32x32,
        2 => SampleSizes.S_64x64,
        3 => SampleSizes.S_128x128,
        4 => SampleSizes.S_256x256,
        5 => SampleSizes.S_512x512,
        6 => SampleSizes.S_1024x1024,
        _ => throw new ArgumentOutOfRangeException(nameof(sampling), "POP sampling must be between 1 and 6.")
    };

    private static string? NormalizeOutputPath(string? filePath, string? requiredExtension, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var fullPath = Path.GetFullPath(filePath.Trim());
        if (requiredExtension != null && !string.Equals(Path.GetExtension(fullPath), requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Output path '{filePath}' must end in {requiredExtension}.", nameof(filePath));
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Output directory does not exist: {directory}");
        if (!overwrite && File.Exists(fullPath))
            throw new IOException($"Output file already exists: {fullPath}. Set overwriteOutputFiles=true to replace it.");
        return fullPath;
    }

    private static string CreateSiblingTempPath(string finalPath, string extension)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        return Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp{extension}");
    }

    private static void WriteGridBinAtomic(
        string path,
        int nx,
        int ny,
        double dx,
        double dy,
        double[][] values,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var tempPath = CreateSiblingTempPath(path, ".bin");
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(nx);
                bw.Write(ny);
                bw.Write(dx);
                bw.Write(dy);
                for (int y = 0; y < ny; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int x = 0; x < nx; x++)
                        bw.Write(values[y][x]);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            CommitTempFile(tempPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private static void CommitTempFile(string tempPath, string finalPath, bool overwrite)
    {
        if (overwrite)
        {
            if (File.Exists(finalPath)) File.Replace(tempPath, finalPath, null);
            else File.Move(tempPath, finalPath);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
    }

    private static void ValidateNonNegativeFinite(double value, string name)
    {
        if (!IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and >= 0.");
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool ApproximatelyEqual(double a, double b)
    {
        var scale = Math.Max(1.0, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= 1e-12 * scale;
    }
}
