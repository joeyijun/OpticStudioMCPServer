using System.ComponentModel;
using System.Reflection;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Analysis;
using ZOSAPI.Analysis.Settings;
using ZOSAPI.Analysis.Settings.ExtendedScene;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class GeometricImageAnalysisTool
{
    private readonly IZemaxSession _session;

    public GeometricImageAnalysisTool(IZemaxSession session) => _session = session;

    public record GiaResult(
        bool Success,
        string? Error = null,
        int Field = 0,
        int Wavelength = 0,
        int Surface = 0,
        int Pixels = 0,
        double ImageSize_mm = 0,
        double PeakIrradiance = 0,
        double TotalPower = 0,
        string? DataMeaning = null,
        string? SettingsDebugInfo = null,
        string? TextFilePath = null,
        string? ImageFilePath = null,
        double[][]? IrradianceData = null);

    [ZemaxTool(Name = "zemax_geometric_image_analysis")]
    [Description(
        "Run Geometric Image Analysis (IMA) without persisting settings or writing export files. "
        + "Returns the grid-producing geometric ray distribution for Surface/Contour/GreyScale/FalseColor display modes. "
        + "Use zemax_export_analysis for filesystem exports. The legacy PeakIrradiance and TotalPower output names represent "
        + "the peak grid value and sum of weighted grid values, not guaranteed physical irradiance/power units.")]
    public async Task<GiaResult> ExecuteAsync(
        [Description("Field number (1-indexed). Null = keep current.")] int? field = null,
        [Description("Wavelength number (0 = polychromatic/all where supported). Null = keep current.")] int? wavelength = null,
        [Description("Surface number to analyze. Null = keep current (typically image surface).")] int? surface = null,
        [Description("Number of pixels across the square grid. Must be positive. Null = keep current.")] int? pixels = null,
        [Description("Number of rays x1000. Must be positive. Null = keep current.")] int? raysX1000 = null,
        [Description("Image size in current lens units. Must be positive. Null = keep current.")] double? imageSize = null,
        [Description("Grid-producing display mode: Surface, Contour, GreyScale, InverseGreyScale, FalseColor, or InverseFalseColor. Null = keep current.")] string? showAs = null,
        [Description("Source type: Uniform or Lambertian. Null = keep current.")] string? source = null,
        [Description("Reference type: ChiefRay, Vertex, PrimaryChief, or Centroid. Null = keep current.")] string? reference = null,
        [Description("Parity: Even or Odd. Null = keep current.")] string? parity = null,
        [Description("IMA source file name (for example LETTERF.IMA). Null = keep current.")] string? file = null,
        [Description("Field size override. Must be finite and non-negative. Null = keep current.")] double? fieldSize = null,
        [Description("Numerical aperture cutoff. Must be finite and non-negative. Null = keep current.")] double? na = null,
        [Description("Image rotation in degrees. Must be finite. Null = keep current.")] double? rotation = null,
        [Description("Total source watts. Must be finite and non-negative. Null = keep current.")] double? totalWatts = null,
        [Description("Row/column number parameter. Must be non-negative. Null = keep current.")] int? rowColumnNumber = null,
        [Description("Scatter rays toggle. Null = keep current.")] bool? scatterRays = null,
        [Description("Use symbols toggle. Null = keep current.")] bool? useSymbols = null,
        [Description("Use polarization toggle. Null = keep current.")] bool? usePolarization = null,
        [Description("Delete vignetted rays toggle. Null = keep current.")] bool? deleteVignetted = null,
        [Description("Remove vignetting factors toggle. Null = keep current.")] bool? removeVignettingFactors = null,
        [Description("Pixel interpolation toggle. Null = keep current.")] bool? usePixelInterpolation = null,
        [Description("Deprecated for this read-only tool. Must be null; use zemax_export_analysis to write text output.")] string? exportTextPath = null,
        [Description("Deprecated for this read-only tool. Must be null; use zemax_export_analysis to write image output.")] string? exportImagePath = null,
        [Description("Return the full 2D grid in the response. Can be large.")] bool returnData = false,
        [Description("Return diagnostic information about the typed IMA settings object.")] bool debugSettings = false,
        [Description("Deprecated for this read-only tool. Must remain false; persistent IMA.CFG writes are not permitted here.")] bool saveSettings = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateInputs(field, wavelength, surface, pixels, raysX1000, imageSize, fieldSize, na,
                rotation, totalWatts, rowColumnNumber, exportTextPath, exportImagePath, saveSettings);

            return await _session.ExecuteAsync("GeometricImageAnalysis", new Dictionary<string, object?>
            {
                ["field"] = field,
                ["wavelength"] = wavelength,
                ["surface"] = surface,
                ["pixels"] = pixels,
                ["raysX1000"] = raysX1000,
                ["imageSize"] = imageSize,
                ["showAs"] = showAs,
                ["source"] = source,
                ["reference"] = reference,
                ["parity"] = parity,
                ["file"] = file,
                ["fieldSize"] = fieldSize,
                ["na"] = na,
                ["rotation"] = rotation,
                ["totalWatts"] = totalWatts,
                ["rowColumnNumber"] = rowColumnNumber,
                ["scatterRays"] = scatterRays,
                ["useSymbols"] = useSymbols,
                ["usePolarization"] = usePolarization,
                ["deleteVignetted"] = deleteVignetted,
                ["removeVignettingFactors"] = removeVignettingFactors,
                ["usePixelInterpolation"] = usePixelInterpolation,
                ["returnData"] = returnData,
                ["debugSettings"] = debugSettings
            }, system =>
            {
                if (field.HasValue && field.Value > system.SystemData.Fields.NumberOfFields)
                    throw new ArgumentOutOfRangeException(nameof(field), $"Field {field.Value} exceeds the system field count ({system.SystemData.Fields.NumberOfFields}).");
                if (wavelength.HasValue && wavelength.Value > system.SystemData.Wavelengths.NumberOfWavelengths)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength {wavelength.Value} exceeds the system wavelength count ({system.SystemData.Wavelengths.NumberOfWavelengths}).");
                int maxSurface = system.LDE.NumberOfSurfaces - 1;
                if (surface.HasValue && surface.Value > maxSurface)
                    throw new ArgumentOutOfRangeException(nameof(surface), $"Surface {surface.Value} exceeds the valid range 0..{maxSurface}.");

                var analysis = system.Analyses.New_Analysis(AnalysisIDM.GeometricImageAnalysis);
                try
                {
                    var settings = analysis.GetSettings() as IAS_GeometricImageAnalysis
                        ?? throw new InvalidOperationException("OpticStudio did not expose typed Geometric Image Analysis settings.");

                    if (field.HasValue) settings.Field.SetFieldNumber(field.Value);
                    if (wavelength.HasValue) settings.Wavelength.SetWavelengthNumber(wavelength.Value);
                    if (surface.HasValue) settings.Surface.SetSurfaceNumber(surface.Value);
                    if (fieldSize.HasValue) settings.FieldSize = fieldSize.Value;
                    if (na.HasValue) settings.NA = na.Value;
                    if (rotation.HasValue) settings.Rotation = rotation.Value;
                    if (totalWatts.HasValue) settings.TotalWatts = totalWatts.Value;
                    if (rowColumnNumber.HasValue) settings.RowColumnNumber = rowColumnNumber.Value;
                    if (scatterRays.HasValue) settings.ScatterRays = scatterRays.Value;
                    if (useSymbols.HasValue) settings.UseSymbols = useSymbols.Value;
                    if (usePolarization.HasValue) settings.UsePolarization = usePolarization.Value;
                    if (deleteVignetted.HasValue) settings.DeleteVignetted = deleteVignetted.Value;
                    if (removeVignettingFactors.HasValue) settings.RemoveVignettingFactors = removeVignettingFactors.Value;
                    if (usePixelInterpolation.HasValue) settings.UsePixelInterpolation = usePixelInterpolation.Value;
                    if (imageSize.HasValue) settings.ImageSize = imageSize.Value;
                    if (pixels.HasValue) settings.NumberOfPixels = pixels.Value;
                    if (raysX1000.HasValue) settings.RaysX1000 = raysX1000.Value;
                    if (file is not null) settings.File = file;
                    if (showAs is not null) settings.ShowAs = ParseNamedEnum<GiaShowAsTypes>(showAs, nameof(showAs));
                    if (source is not null) settings.Source = ParseNamedEnum<SourceGia>(source, nameof(source));
                    if (reference is not null) settings.Reference = ParseNamedEnum<ReferenceGia>(reference, nameof(reference));
                    if (parity is not null) settings.Parity = ParseNamedEnum<Parity>(parity, nameof(parity));

                    if (!IsGridMode(settings.ShowAs))
                    {
                        throw new InvalidOperationException(
                            $"Geometric Image Analysis mode '{settings.ShowAs}' does not provide the grid contract returned by this tool. "
                            + "Use Surface/Contour/GreyScale/InverseGreyScale/FalseColor/InverseFalseColor, or use zemax_export_analysis for display-only modes.");
                    }

                    string? settingsDebugInfo = debugSettings ? DescribeSettingsObject(settings) : null;
                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Geometric Image Analysis returned no results object.");
                    var dataGrid = results.GetDataGrid(0) ?? throw new InvalidOperationException("Geometric Image Analysis returned no data grid for the selected display mode.");

                    int nx = checked((int)dataGrid.Nx);
                    int ny = checked((int)dataGrid.Ny);
                    if (nx <= 0 || ny <= 0)
                        throw new InvalidOperationException("Geometric Image Analysis returned an empty data grid.");

                    double[][]? gridData = returnData ? new double[ny][] : null;
                    double peakGridValue = double.NegativeInfinity;
                    double gridSum = 0;
                    for (int j = 0; j < ny; j++)
                    {
                        if (returnData) gridData![j] = new double[nx];
                        for (int i = 0; i < nx; i++)
                        {
                            double value = dataGrid.Z(i, j);
                            if (double.IsNaN(value) || double.IsInfinity(value))
                                throw new InvalidOperationException($"Geometric Image Analysis returned a non-finite grid value at ({i}, {j}).");
                            if (returnData) gridData![j][i] = value;
                            if (value > peakGridValue) peakGridValue = value;
                            gridSum += value;
                        }
                    }

                    double imageSizeMm = settings.ImageSize * LensUnitToMillimeters(system.SystemData.Units.LensUnits.ToString());
                    return new GiaResult(
                        Success: true,
                        Field: settings.Field.GetFieldNumber(),
                        Wavelength: settings.Wavelength.GetWavelengthNumber(),
                        Surface: settings.Surface.GetSurfaceNumber(),
                        Pixels: nx,
                        ImageSize_mm: imageSizeMm,
                        PeakIrradiance: peakGridValue,
                        TotalPower: gridSum,
                        DataMeaning: "PeakIrradiance and TotalPower are legacy field names containing the peak and sum of the Geometric Image Analysis weighted-ray grid; they are not guaranteed physical irradiance/power units.",
                        SettingsDebugInfo: settingsDebugInfo,
                        IrradianceData: gridData);
                }
                finally
                {
                    analysis.Close();
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new GiaResult(false, Error: ex.Message);
        }
    }

    private static void ValidateInputs(
        int? field, int? wavelength, int? surface, int? pixels, int? raysX1000,
        double? imageSize, double? fieldSize, double? na, double? rotation, double? totalWatts,
        int? rowColumnNumber, string? exportTextPath, string? exportImagePath, bool saveSettings)
    {
        if (field.HasValue && field.Value <= 0) throw new ArgumentOutOfRangeException(nameof(field), "Field must be >= 1.");
        if (wavelength.HasValue && wavelength.Value < 0) throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be >= 0.");
        if (surface.HasValue && surface.Value < 0) throw new ArgumentOutOfRangeException(nameof(surface), "Surface must be >= 0.");
        if (pixels.HasValue && pixels.Value <= 0) throw new ArgumentOutOfRangeException(nameof(pixels), "Pixels must be > 0.");
        if (raysX1000.HasValue && raysX1000.Value <= 0) throw new ArgumentOutOfRangeException(nameof(raysX1000), "raysX1000 must be > 0.");
        ValidateFinite(imageSize, nameof(imageSize), requirePositive: true);
        ValidateFinite(fieldSize, nameof(fieldSize), requireNonNegative: true);
        ValidateFinite(na, nameof(na), requireNonNegative: true);
        ValidateFinite(rotation, nameof(rotation));
        ValidateFinite(totalWatts, nameof(totalWatts), requireNonNegative: true);
        if (rowColumnNumber.HasValue && rowColumnNumber.Value < 0)
            throw new ArgumentOutOfRangeException(nameof(rowColumnNumber), "rowColumnNumber must be >= 0.");
        if (!string.IsNullOrWhiteSpace(exportTextPath) || !string.IsNullOrWhiteSpace(exportImagePath))
            throw new InvalidOperationException("zemax_geometric_image_analysis is ReadOnly and does not write files. Use zemax_export_analysis for filesystem exports.");
        if (saveSettings)
            throw new InvalidOperationException("zemax_geometric_image_analysis is ReadOnly and cannot persist IMA.CFG settings.");
    }

    private static void ValidateFinite(double? value, string name, bool requirePositive = false, bool requireNonNegative = false)
    {
        if (!value.HasValue) return;
        if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite.");
        if (requirePositive && value.Value <= 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be > 0.");
        if (requireNonNegative && value.Value < 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be >= 0.");
    }

    private static TEnum ParseNamedEnum<TEnum>(string value, string parameterName) where TEnum : struct
    {
        var match = Enum.GetNames(typeof(TEnum)).FirstOrDefault(name => name.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (match == null)
            throw new ArgumentException($"Invalid {parameterName} '{value}'. Allowed values: {string.Join(", ", Enum.GetNames(typeof(TEnum)))}.", parameterName);
        return (TEnum)Enum.Parse(typeof(TEnum), match, ignoreCase: false);
    }

    private static bool IsGridMode(GiaShowAsTypes showAs) =>
        showAs == GiaShowAsTypes.Surface ||
        showAs == GiaShowAsTypes.Contour ||
        showAs == GiaShowAsTypes.GreyScale ||
        showAs == GiaShowAsTypes.InverseGreyScale ||
        showAs == GiaShowAsTypes.FalseColor ||
        showAs == GiaShowAsTypes.InverseFalseColor;

    private static double LensUnitToMillimeters(string lensUnits) => lensUnits switch
    {
        "Millimeters" => 1.0,
        "Centimeters" => 10.0,
        "Inches" => 25.4,
        "Meters" => 1000.0,
        _ => throw new InvalidOperationException($"Unsupported lens-unit value '{lensUnits}' while converting Geometric Image Analysis size to millimeters.")
    };

    private static string DescribeSettingsObject(object settingsObj)
    {
        var type = settingsObj.GetType();
        var props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p =>
            {
                try { return $"{p.PropertyType.Name} {p.Name} = {p.GetValue(settingsObj)}"; }
                catch { return $"{p.PropertyType.Name} {p.Name} = <error>"; }
            })
            .OrderBy(s => s)
            .ToArray();
        return string.Join(Environment.NewLine, new[]
        {
            $"Settings runtime type: {type.FullName}",
            "Properties:",
            props.Length > 0 ? string.Join(Environment.NewLine, props) : "<none>"
        });
    }
}
