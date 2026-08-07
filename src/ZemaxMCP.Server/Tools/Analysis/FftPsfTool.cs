using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Analysis.Settings.Psf;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class FftPsfTool
{
    private const int InlineGridCellLimit = 65536;
    private readonly IZemaxSession _session;

    public FftPsfTool(IZemaxSession session) => _session = session;

    public record FftPsfResult(
        bool Success,
        string? Error = null,
        int? Nx = null, int? Ny = null,
        double? Dx = null, double? Dy = null,
        double[]? Grid = null,
        double? StrehlRatio = null,
        string? Field = null,
        string? Wavelength = null,
        string? TextPath = null,
        string? GridPath = null,
        IReadOnlyList<string>? Warnings = null);

    [ZemaxTool(Name = "zemax_fft_psf")]
    [Description(
        "Run FFT Point Spread Function analysis and return the PSF intensity/phase grid. "
        + "sampleSize/outputSize/type must be names present in the installed ZOS-API enums; invalid names are rejected instead of silently using defaults. "
        + "Large grids require gridPath. FFT PSF does not reliably expose Strehl in all ZOS-API versions; use zemax_huygens_psf when Strehl is required.")]
    public async Task<FftPsfResult> ExecuteAsync(
        [Description("Wavelength number (1-indexed); 0 = primary")] int wavelength = 0,
        [Description("Field number (1-indexed)")] int field = 1,
        [Description("Surface number; 0 = image") ] int surface = 0,
        [Description("PsfSampling enum name, e.g. PsfS_64x64, PsfS_128x128, PsfS_256x256")] string sampleSize = "PsfS_128x128",
        [Description("PsfSampling enum name for output sampling, e.g. PsfS_64x64")] string outputSize = "PsfS_64x64",
        [Description("FftPsfType enum name, e.g. Linear, Log, Phase, Real, or Imaginary when supported by the installed version")] string type = "Linear",
        [Description("Image plane pixel size in micrometers; 0 = automatic")] double imageDelta = 0.0,
        [Description("Normalize the PSF")] bool normalize = true,
        [Description("Use polarization")] bool usePolarization = false,
        [Description("Optional output text path") ] string? textPath = null,
        [Description("Optional raw float64 grid path; required when grid exceeds the inline limit")] string? gridPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (wavelength < 0) throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be 0 (primary) or a positive wavelength number.");
            if (field < 1) throw new ArgumentOutOfRangeException(nameof(field), "Field number must be at least 1.");
            if (surface < 0) throw new ArgumentOutOfRangeException(nameof(surface), "Surface must be 0 (image) or a positive surface number.");
            if (double.IsNaN(imageDelta) || double.IsInfinity(imageDelta) || imageDelta < 0)
                throw new ArgumentOutOfRangeException(nameof(imageDelta), "Image delta must be finite and non-negative.");

            var sampleEnum = ParseNamedEnum<PsfSampling>(sampleSize, nameof(sampleSize));
            var outputEnum = ParseNamedEnum<PsfSampling>(outputSize, nameof(outputSize));
            var typeEnum = ParseNamedEnum<FftPsfType>(type, nameof(type));
            textPath = NormalizeOptionalPath(textPath);
            gridPath = NormalizeOptionalPath(gridPath);

            var parameters = new Dictionary<string, object?>
            {
                ["wavelength"] = wavelength, ["field"] = field, ["surface"] = surface,
                ["sampleSize"] = sampleEnum.ToString(), ["outputSize"] = outputEnum.ToString(),
                ["type"] = typeEnum.ToString(), ["imageDelta"] = imageDelta,
                ["normalize"] = normalize, ["usePolarization"] = usePolarization,
                ["textPath"] = textPath, ["gridPath"] = gridPath
            };

            return await _session.ExecuteAsync("FftPsf", parameters, system =>
            {
                if (field > system.SystemData.Fields.NumberOfFields)
                    throw new ArgumentOutOfRangeException(nameof(field), $"Field must be between 1 and {system.SystemData.Fields.NumberOfFields}.");
                if (wavelength > system.SystemData.Wavelengths.NumberOfWavelengths)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be 0 or between 1 and {system.SystemData.Wavelengths.NumberOfWavelengths}.");
                var lastSurface = system.LDE.NumberOfSurfaces - 1;
                if (surface > lastSurface)
                    throw new ArgumentOutOfRangeException(nameof(surface), $"Surface must be 0 (image) or between 1 and {lastSurface}.");

                var analysis = system.Analyses.New_Analysis_SettingsFirst(ZOSAPI.Analysis.AnalysisIDM.FftPsf);
                if (analysis == null) throw new InvalidOperationException("OpticStudio did not create an FFT PSF analysis.");
                try
                {
                    if (analysis.GetSettings() is not IAS_FftPsf settings)
                        throw new InvalidOperationException("OpticStudio did not expose FFT PSF settings through IAS_FftPsf.");

                    if (wavelength > 0) settings.Wavelength.SetWavelengthNumber(wavelength);
                    if (field > 0) settings.Field.SetFieldNumber(field);
                    if (surface == 0) settings.Surface.UseImageSurface();
                    else settings.Surface.SetSurfaceNumber(surface);
                    settings.SampleSize = sampleEnum;
                    settings.OutputSize = outputEnum;
                    settings.Type = typeEnum;

                    var warnings = new List<string>();
                    dynamic dynamicSettings = settings;
                    TryApplyOptionalSetting(() => dynamicSettings.ImageDelta = imageDelta, "ImageDelta", warnings);
                    TryApplyOptionalSetting(() => dynamicSettings.Normalize = normalize, "Normalize", warnings);
                    TryApplyOptionalSetting(() => dynamicSettings.UsePolarization = usePolarization, "UsePolarization", warnings);

                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("FFT PSF returned no results object.");

                    var temporaryText = textPath ?? Path.Combine(Path.GetTempPath(), $"zemax_fft_psf_{Guid.NewGuid():N}.txt");
                    double? strehl = null;
                    string? fieldLabel = null;
                    string? waveLabel = null;
                    try
                    {
                        results.GetTextFile(temporaryText);
                        if (File.Exists(temporaryText))
                            (strehl, fieldLabel, waveLabel) = ParsePsfHeader(temporaryText);
                        else if (textPath != null)
                            warnings.Add("OpticStudio did not create the requested FFT PSF text file.");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("FFT PSF text export/header parsing failed: " + ex.Message);
                    }

                    dynamic resultsDyn = results;
                    dynamic? grid = null;
                    try { grid = resultsDyn.GetDataGrid(0); } catch { }
                    if (grid == null) { try { grid = resultsDyn.GetDataGridDouble(0); } catch { } }
                    if (grid == null)
                        return new FftPsfResult(false, Error: "FFT PSF produced no data grid. Check sampling/type settings.", Warnings: warnings);

                    var nx = (int)grid.Nx;
                    var ny = (int)grid.Ny;
                    var dx = (double)grid.Dx;
                    var dy = (double)grid.Dy;
                    if (nx <= 0 || ny <= 0) return new FftPsfResult(false, Error: $"FFT PSF returned an invalid grid size {nx}x{ny}.", Warnings: warnings);
                    var cells = checked(nx * ny);

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
                        return new FftPsfResult(false, Error: "Unable to read PSF data grid through the supported ZOS-API grid access patterns.", Warnings: warnings);

                    var flat = new double[cells];
                    for (var y = 0; y < ny; y++)
                        for (var x = 0; x < nx; x++)
                            flat[y * nx + x] = reader(y, x);

                    double[]? inlineGrid = null;
                    string? gridOut = null;
                    if (cells <= InlineGridCellLimit && string.IsNullOrEmpty(gridPath))
                    {
                        inlineGrid = flat;
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(gridPath))
                            return new FftPsfResult(false,
                                Error: $"Grid {nx}x{ny}={cells} exceeds inline limit {InlineGridCellLimit}. Provide gridPath.", Warnings: warnings);
                        EnsureDirectory(gridPath);
                        WriteGridBin(gridPath, nx, ny, dx, dy, flat);
                        gridOut = gridPath;
                    }

                    if (textPath == null) { try { File.Delete(temporaryText); } catch { } }

                    return new FftPsfResult(true, Nx: nx, Ny: ny, Dx: dx, Dy: dy,
                        Grid: inlineGrid, StrehlRatio: strehl,
                        Field: fieldLabel, Wavelength: waveLabel,
                        TextPath: textPath, GridPath: gridOut,
                        Warnings: warnings.Count > 0 ? warnings : null);
                }
                finally
                {
                    analysis.Close();
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new FftPsfResult(false, Error: ex.Message);
        }
    }

    private static T ParseNamedEnum<T>(string value, string parameterName) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(parameterName + " is required.", parameterName);
        var name = Enum.GetNames(typeof(T)).FirstOrDefault(candidate => string.Equals(candidate, value.Trim(), StringComparison.OrdinalIgnoreCase));
        if (name == null || !Enum.TryParse<T>(name, false, out var parsed))
            throw new ArgumentException($"Unsupported {parameterName} '{value}'. Valid values: {string.Join(", ", Enum.GetNames(typeof(T)))}.", parameterName);
        return parsed;
    }

    private static string? NormalizeOptionalPath(string? path)
    {
        if (path == null) return null;
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Output path cannot be blank; omit it when no file is required.");
        return Path.GetFullPath(path);
    }

    private static void TryApplyOptionalSetting(Action setter, string settingName, List<string> warnings)
    {
        try { setter(); }
        catch (Exception ex) { warnings.Add($"{settingName} is not supported by this OpticStudio/ZOS-API version: {ex.Message}"); }
    }

    private static (double? strehl, string? field, string? wave) ParsePsfHeader(string path)
    {
        double? strehl = null;
        string? field = null;
        string? wave = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            var lower = line.ToLowerInvariant();
            if (strehl == null && lower.Contains("strehl"))
            {
                foreach (var token in line.Split([' ', '\t', ':'], StringSplitOptions.RemoveEmptyEntries))
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    { strehl = value; break; }
            }
            else if (field == null && lower.StartsWith("field") && line.Contains(':'))
                field = line.Substring(line.IndexOf(':') + 1).Trim();
            else if (wave == null && (lower.StartsWith("wave") || lower.StartsWith("wavelength")) && line.Contains(':'))
                wave = line.Substring(line.IndexOf(':') + 1).Trim();
        }
        return (strehl, field, wave);
    }

    private static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);
    }

    private static void WriteGridBin(string path, int nx, int ny, double dx, double dy, double[] flat)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(nx);
        writer.Write(ny);
        writer.Write(dx);
        writer.Write(dy);
        foreach (var value in flat) writer.Write(value);
    }
}
