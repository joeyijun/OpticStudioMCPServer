using System.ComponentModel;
using System.Globalization;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;
using ZOSAPI.Analysis;
using ZOSAPI.Analysis.Settings;
using ZOSAPI.Analysis.Settings.Psf;

namespace ZemaxMCP.Server.Tools.Analysis;

// 说明:接口段从历史会话记录恢复(逐字复刻);设置应用段(原记录在 lambda 起始处截断)
// 按反射得到的 IAS_HuygensPsf 权威成员名强类型重建;结果提取段按 main 现有 PopTool 写法重建。
// 运行时数值语义待 OpticStudio ZOSAPI 连接可用后核验。
[McpServerToolType]
public class HuygensPsfTool
{
    private const int InlineGridCellLimit = 65536;

    private readonly IZemaxSession _session;

    public HuygensPsfTool(IZemaxSession session) => _session = session;

    public record HuygensPsfResult(
        bool Success,
        string? Error = null,
        int? Nx = null, int? Ny = null,
        double? Dx = null, double? Dy = null,
        double[]? Grid = null,
        double? StrehlRatio = null,
        string? Field = null,
        string? Wavelength = null,
        string? TextPath = null,
        string? GridPath = null);

    [McpServerTool(Name = "zemax_huygens_psf")]
    [Description(
        "Run Huygens Point Spread Function analysis. More accurate than FFT PSF near caustics or "
        + "in highly aberrated systems (uses physical wavelet superposition rather than FFT). "
        + "Slower than FFT PSF; recommended when forward-model accuracy matters (curvature WFS).")]
    public async Task<HuygensPsfResult> ExecuteAsync(
        [Description("Wavelength number (1-indexed); 0 = primary")] int wavelength = 0,
        [Description("Field number (1-indexed)")] int field = 1,
        [Description("Pupil sampling enum value (e.g., 'S_64x64', 'S_128x128', 'S_256x256')")]
        string pupilSampleSize = "S_128x128",
        [Description("Image sampling enum value")] string imageSampleSize = "S_64x64",
        [Description("Output type (HuygensPsfTypes): 'Linear', 'Log_Minus_1'..'Log_Minus_5', 'Real', 'Imaginary' (default Linear)")] string type = "Linear",
        [Description("Image plane pixel size in micrometers (0 = auto)")] double imageDelta = 0.0,
        [Description("Normalize PSF (true)")] bool normalize = true,
        [Description("Use centroid as reference (true)")] bool useCentroid = true,
        [Description("Use polarization (default false)")] bool usePolarization = false,
        [Description("Optional output text path (.txt)")] string? textPath = null,
        [Description("Optional path for raw float64 grid (required if grid > 65536 cells)")] string? gridPath = null)
    {
        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["wavelength"] = wavelength, ["field"] = field,
                ["pupilSampleSize"] = pupilSampleSize, ["imageSampleSize"] = imageSampleSize,
                ["type"] = type, ["imageDelta"] = imageDelta, ["normalize"] = normalize,
                ["useCentroid"] = useCentroid, ["usePolarization"] = usePolarization,
                ["textPath"] = textPath, ["gridPath"] = gridPath
            };

            return await _session.ExecuteAsync("HuygensPsf", parameters, system =>
            {
                var analysis = system.Analyses.New_Analysis_SettingsFirst(
                    ZOSAPI.Analysis.AnalysisIDM.HuygensPsf);
                try
                {
                    var s = analysis.GetSettings() as IAS_HuygensPsf;
                    if (s != null)
                    {
                        if (wavelength > 0) s.Wavelength.SetWavelengthNumber(wavelength);
                        if (field > 0) s.Field.SetFieldNumber(field);
                        if (Enum.TryParse<SampleSizes>(pupilSampleSize, ignoreCase: true, out var psEnum))
                            s.PupilSampleSize = psEnum;
                        if (Enum.TryParse<SampleSizes>(imageSampleSize, ignoreCase: true, out var isEnum))
                            s.ImageSampleSize = isEnum;
                        if (Enum.TryParse<HuygensPsfTypes>(type, ignoreCase: true, out var typeEnum))
                            s.Type = typeEnum;
                        s.ImageDelta = imageDelta;
                        s.Normalize = normalize;
                        s.UseCentroid = useCentroid;
                        s.UsePolarization = usePolarization;
                    }

                    analysis.ApplyAndWaitForCompletion();

                    var results = analysis.GetResults();

                    string tmpTxt = textPath ?? Path.Combine(
                        Path.GetTempPath(), $"zemax_huygens_psf_{Guid.NewGuid():N}.txt");
                    double? strehl = null; string? fieldLabel = null, waveLabel = null;
                    try
                    {
                        results.GetTextFile(tmpTxt);
                        (strehl, fieldLabel, waveLabel) = ParsePsfHeader(tmpTxt);
                    }
                    catch { }

                    dynamic resultsDyn = results;
                    dynamic? grid = null;
                    try { grid = resultsDyn.GetDataGrid(0); } catch { }
                    if (grid == null) { try { grid = resultsDyn.GetDataGridDouble(0); } catch { } }
                    if (grid == null)
                        return new HuygensPsfResult(false,
                            Error: "Huygens PSF produced no data grid. Check sampling/type settings.");

                    int nx = (int)grid.Nx;
                    int ny = (int)grid.Ny;
                    double dx = (double)grid.Dx;
                    double dy = (double)grid.Dy;

                    Func<int, int, double>? reader = null;
                    try { _ = (double)grid.Z(0, 0); reader = (y, x) => (double)grid.Z(x, y); }
                    catch
                    {
                        try { _ = (double)grid.Values[0, 0]; reader = (y, x) => (double)grid.Values[y, x]; }
                        catch
                        {
                            try { _ = (double)grid.Values(0, 0); reader = (y, x) => (double)grid.Values(y, x); }
                            catch { }
                        }
                    }
                    if (reader == null)
                        return new HuygensPsfResult(false,
                            Error: "Unable to read PSF data grid: Z(x,y)/Values[y,x]/Values(y,x) all failed.");

                    var flat = new double[nx * ny];   // row-major (y*nx + x)
                    for (int y = 0; y < ny; y++)
                        for (int x = 0; x < nx; x++)
                            flat[y * nx + x] = reader(y, x);

                    int cells = nx * ny;
                    double[]? inlineGrid = null; string? gridOut = null;
                    if (cells <= InlineGridCellLimit && string.IsNullOrEmpty(gridPath))
                    {
                        inlineGrid = flat;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(gridPath))
                            return new HuygensPsfResult(false,
                                Error: $"Grid {nx}x{ny}={cells} exceeds inline limit {InlineGridCellLimit}. Provide gridPath.");
                        EnsureDirectory(gridPath);
                        WriteGridBin(gridPath!, nx, ny, dx, dy, flat);
                        gridOut = gridPath;
                    }

                    if (textPath == null) { try { File.Delete(tmpTxt); } catch { } }

                    return new HuygensPsfResult(true, Nx: nx, Ny: ny, Dx: dx, Dy: dy,
                        Grid: inlineGrid, StrehlRatio: strehl,
                        Field: fieldLabel, Wavelength: waveLabel,
                        TextPath: textPath, GridPath: gridOut);
                }
                finally
                {
                    analysis.Close();
                }
            });
        }
        catch (Exception ex)
        {
            return new HuygensPsfResult(false, Error: ex.Message);
        }
    }

    private static (double? strehl, string? field, string? wave) ParsePsfHeader(string path)
    {
        double? strehl = null; string? field = null, wave = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var lower = line.ToLowerInvariant();
            if (strehl == null && lower.Contains("strehl"))
            {
                foreach (var tok in line.Split([' ', '\t', ':'], StringSplitOptions.RemoveEmptyEntries))
                    if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    { strehl = v; break; }
            }
            else if (field == null && lower.StartsWith("field") && line.Contains(':'))
                field = line[(line.IndexOf(':') + 1)..].Trim();
            else if (wave == null && (lower.StartsWith("wave") || lower.StartsWith("wavelength")) && line.Contains(':'))
                wave = line[(line.IndexOf(':') + 1)..].Trim();
        }
        return (strehl, field, wave);
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
    }

    // 二进制格式与 PopTool.WriteGridBin 一致:int32 nx, int32 ny, float64 dx, float64 dy, 然后 nx*ny 个 float64(行主序)。
    private static void WriteGridBin(string path, int nx, int ny, double dx, double dy, double[] flat)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(nx);
        bw.Write(ny);
        bw.Write(dx);
        bw.Write(dy);
        for (int i = 0; i < flat.Length; i++)
            bw.Write(flat[i]);
    }
}
