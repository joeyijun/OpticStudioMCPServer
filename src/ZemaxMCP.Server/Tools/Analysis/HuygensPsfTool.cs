using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Analysis.Settings;
using ZOSAPI.Analysis.Settings.Psf;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
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
        string? GridPath = null,
        IReadOnlyList<string>? Warnings = null);

    [ZemaxTool(Name = "zemax_huygens_psf")]
    [Description(
        "Run Huygens Point Spread Function analysis. Pupil/image sampling and output type must be named enum values supported by the installed ZOS-API; invalid names are rejected instead of silently using defaults. "
        + "Large grids require gridPath. Huygens PSF is slower than FFT PSF but is useful for highly aberrated/near-caustic systems.")]
    public async Task<HuygensPsfResult> ExecuteAsync(
        [Description("Wavelength number (1-indexed); 0 = primary")] int wavelength = 0,
        [Description("Field number (1-indexed)")] int field = 1,
        [Description("SampleSizes enum name, e.g. S_64x64, S_128x128, S_256x256")] string pupilSampleSize = "S_128x128",
        [Description("SampleSizes enum name for the image grid")] string imageSampleSize = "S_64x64",
        [Description("HuygensPsfTypes enum name, e.g. Linear, Log_Minus_1, Real, or Imaginary when supported")] string type = "Linear",
        [Description("Image plane pixel size in micrometers; 0 = automatic")] double imageDelta = 0.0,
        [Description("Normalize PSF")] bool normalize = true,
        [Description("Use centroid as reference")] bool useCentroid = true,
        [Description("Use polarization")] bool usePolarization = false,
        [Description("Optional output text path")] string? textPath = null,
        [Description("Optional raw float64 grid path; required when grid exceeds the inline limit")] string? gridPath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (wavelength < 0) throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be 0 (primary) or a positive wavelength number.");
            if (field < 1) throw new ArgumentOutOfRangeException(nameof(field), "Field number must be at least 1.");
            if (double.IsNaN(imageDelta) || double.IsInfinity(imageDelta) || imageDelta < 0)
                throw new ArgumentOutOfRangeException(nameof(imageDelta), "Image delta must be finite and non-negative.");

            var pupilEnum = ParseNamedEnum<SampleSizes>(pupilSampleSize, nameof(pupilSampleSize));
            var imageEnum = ParseNamedEnum<SampleSizes>(imageSampleSize, nameof(imageSampleSize));
            var typeEnum = ParseNamedEnum<HuygensPsfTypes>(type, nameof(type));
            textPath = NormalizeOptionalPath(textPath);
            gridPath = NormalizeOptionalPath(gridPath);

            var parameters = new Dictionary<string, object?>
            {
                ["wavelength"] = wavelength, ["field"] = field,
                ["pupilSampleSize"] = pupilEnum.ToString(), ["imageSampleSize"] = imageEnum.ToString(),
                ["type"] = typeEnum.ToString(), ["imageDelta"] = imageDelta, ["normalize"] = normalize,
                ["useCentroid"] = useCentroid, ["usePolarization"] = usePolarization,
                ["textPath"] = textPath, ["gridPath"] = gridPath
            };

            return await _session.ExecuteAsync("HuygensPsf", parameters, system =>
            {
                if (field > system.SystemData.Fields.NumberOfFields)
                    throw new ArgumentOutOfRangeException(nameof(field), $"Field must be between 1 and {system.SystemData.Fields.NumberOfFields}.");
                if (wavelength > system.SystemData.Wavelengths.NumberOfWavelengths)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be 0 or between 1 and {system.SystemData.Wavelengths.NumberOfWavelengths}.");

                var analysis = system.Analyses.New_Analysis_SettingsFirst(ZOSAPI.Analysis.AnalysisIDM.HuygensPsf);
                if (analysis == null) throw new InvalidOperationException("OpticStudio did not create a Huygens PSF analysis.");
                try
                {
                    if (analysis.GetSettings() is not IAS_HuygensPsf settings)
                        throw new InvalidOperationException("OpticStudio did not expose Huygens PSF settings through IAS_HuygensPsf.");

                    if (wavelength > 0) settings.Wavelength.SetWavelengthNumber(wavelength);
                    settings.Field.SetFieldNumber(field);
                    settings.PupilSampleSize = pupilEnum;
                    settings.ImageSampleSize = imageEnum;
                    settings.Type = typeEnum;
                    settings.ImageDelta = imageDelta;
                    settings.Normalize = normalize;
                    settings.UseCentroid = useCentroid;
                    settings.UsePolarization = usePolarization;

                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Huygens PSF returned no results object.");
                    var warnings = new List<string>();

                    var temporaryText = textPath ?? Path.Combine(Path.GetTempPath(), $"zemax_huygens_psf_{Guid.NewGuid():N}.txt");
                    double? strehl = null;
                    string? fieldLabel = null;
                    string? waveLabel = null;
                    try
                    {
                        results.GetTextFile(temporaryText);
                        if (File.Exists(temporaryText))
                            (strehl, fieldLabel, waveLabel) = ParsePsfHeader(temporaryText);
                        else if (textPath != null)
                            warnings.Add("OpticStudio did not create the requested Huygens PSF text file.");
                    }
                    catch (Exception ex)
                    {
                        warnings.Add("Huygens PSF text export/header parsing failed: " + ex.Message);
                    }

                    dynamic resultsDyn = results;
                    dynamic? grid = null;
                    try { grid = resultsDyn.GetDataGrid(0); } catch { }
                    if (grid == null) { try { grid = resultsDyn.GetDataGridDouble(0); } catch { } }
                    if (grid == null)
                        return new HuygensPsfResult(false, Error: "Huygens PSF produced no data grid. Check sampling/type settings.", Warnings: warnings);

                    var nx = (int)grid.Nx;
                    var ny = (int)grid.Ny;
                    var dx = (double)grid.Dx;
                    var dy = (double)grid.Dy;
                    if (nx <= 0 || ny <= 0)
                        return new HuygensPsfResult(false, Error: $"Huygens PSF returned an invalid grid size {nx}x{ny}.", Warnings: warnings);
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
                        return new HuygensPsfResult(false, Error: "Unable to read Huygens PSF data grid through the supported ZOS-API grid access patterns.", Warnings: warnings);

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
                            return new HuygensPsfResult(false,
                                Error: $"Grid {nx}x{ny}={cells} exceeds inline limit {InlineGridCellLimit}. Provide gridPath.", Warnings: warnings);
                        EnsureDirectory(gridPath);
                        WriteGridBin(gridPath, nx, ny, dx, dy, flat);
                        gridOut = gridPath;
                    }

                    if (textPath == null) { try { File.Delete(temporaryText); } catch { } }

                    return new HuygensPsfResult(true, Nx: nx, Ny: ny, Dx: dx, Dy: dy,
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
            return new HuygensPsfResult(false, Error: ex.Message);
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
