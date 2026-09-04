using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class FieldCurvatureDistortionTool
{
    private readonly IZemaxSession _session;

    public FieldCurvatureDistortionTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_field_curvature_distortion")]
    [Description("Calculate field curvature and distortion versus field for each system wavelength. distortionType must be f_tan_theta or f_theta.")]
    public async Task<FieldCurvatureDistortionData> ExecuteAsync(
        [Description("Distortion type: f_tan_theta (default) or f_theta")] string distortionType = "f_tan_theta",
        CancellationToken cancellationToken = default)
    {
        try
        {
            var normalizedType = distortionType?.Trim().ToLowerInvariant() switch
            {
                "f_tan_theta" => "f_tan_theta",
                "f_theta" => "f_theta",
                _ => throw new ArgumentException("distortionType must be f_tan_theta or f_theta.", nameof(distortionType))
            };

            return await _session.ExecuteAsync("FieldCurvatureDistortion",
                new Dictionary<string, object?> { ["distortionType"] = normalizedType }, system =>
                {
                    var analysis = system.Analyses.New_FieldCurvatureAndDistortion();
                    if (analysis == null)
                        throw new InvalidOperationException("OpticStudio did not create a Field Curvature and Distortion analysis.");
                    try
                    {
                        if (analysis.GetSettings() is not ZOSAPI.Analysis.Settings.Aberrations.IAS_FieldCurvatureAndDistortion settings)
                            throw new InvalidOperationException("OpticStudio did not expose Field Curvature and Distortion settings through IAS_FieldCurvatureAndDistortion.");
                        settings.Distortion = normalizedType == "f_theta"
                            ? ZOSAPI.Analysis.Settings.Aberrations.Distortions.F_Theta
                            : ZOSAPI.Analysis.Settings.Aberrations.Distortions.F_TanTheta;

                        analysis.ApplyAndWaitForCompletion();
                        var results = analysis.GetResults() ?? throw new InvalidOperationException("Field Curvature and Distortion returned no results object.");
                        var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_fcd_{Guid.NewGuid():N}.txt");
                        try
                        {
                            results.GetTextFile(tempFile);
                            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                                throw new IOException("Field Curvature and Distortion produced no text results.");
                            return ParseTextFile(tempFile);
                        }
                        finally
                        {
                            try { File.Delete(tempFile); } catch { }
                        }
                    }
                    finally
                    {
                        analysis.Close();
                    }
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new FieldCurvatureDistortionData { Success = false, Error = ex.Message };
        }
    }

    private static FieldCurvatureDistortionData ParseTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        string distortionType = "", shiftUnits = "", heightUnits = "", distortionUnits = "";
        double maxField = double.NaN, maxDistortion = double.NaN;
        var wavelengthBlocks = new List<FieldCurvatureWavelengthData>();
        double currentWavelength = double.NaN, currentFocalLength = double.NaN;
        var fieldAngles = new List<double>();
        var tanShifts = new List<double>();
        var sagShifts = new List<double>();
        var realHeights = new List<double>();
        var refHeights = new List<double>();
        var distortions = new List<double>();
        bool inData = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Distortion Type", StringComparison.OrdinalIgnoreCase))
            {
                var idx = trimmed.IndexOf(':');
                if (idx >= 0) distortionType = trimmed.Substring(idx + 1).Trim();
                continue;
            }
            if (trimmed.StartsWith("Shift units", StringComparison.OrdinalIgnoreCase)) { shiftUnits = ParseUnits(trimmed); continue; }
            if (trimmed.StartsWith("Height units", StringComparison.OrdinalIgnoreCase)) { heightUnits = ParseUnits(trimmed); continue; }
            if (trimmed.StartsWith("Distortion units", StringComparison.OrdinalIgnoreCase)) { distortionUnits = ParseUnits(trimmed); continue; }
            if (trimmed.StartsWith("Maximum Field", StringComparison.OrdinalIgnoreCase)) { maxField = ParseValueBeforeUnit(trimmed); continue; }
            if (trimmed.StartsWith("Maximum distortion", StringComparison.OrdinalIgnoreCase)) { maxDistortion = ParseValueAfterEquals(trimmed); continue; }

            if (trimmed.StartsWith("Data for wavelength", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrentBlock();
                currentWavelength = ParseColonValue(trimmed);
                currentFocalLength = double.NaN;
                fieldAngles = new List<double>();
                tanShifts = new List<double>();
                sagShifts = new List<double>();
                realHeights = new List<double>();
                refHeights = new List<double>();
                distortions = new List<double>();
                inData = false;
                continue;
            }
            if (trimmed.StartsWith("Distortion focal length", StringComparison.OrdinalIgnoreCase)) { currentFocalLength = ParseEqualsValue(trimmed); continue; }
            if (trimmed.StartsWith("Y Angle", StringComparison.OrdinalIgnoreCase)) { inData = true; continue; }
            if (!inData) continue;

            var values = trimmed.Replace("%", "").Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 6 &&
                double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var angle) &&
                double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tanS) &&
                double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sagS) &&
                double.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var realH) &&
                double.TryParse(values[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var refH) &&
                double.TryParse(values[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var dist))
            {
                fieldAngles.Add(angle);
                tanShifts.Add(tanS);
                sagShifts.Add(sagS);
                realHeights.Add(realH);
                refHeights.Add(refH);
                distortions.Add(dist);
            }
        }
        AddCurrentBlock();

        if (wavelengthBlocks.Count == 0)
            throw new InvalidDataException("Field Curvature and Distortion text results contained no parsable wavelength blocks. The installed OpticStudio text format may be unsupported.");

        return new FieldCurvatureDistortionData
        {
            Success = true,
            DistortionType = distortionType,
            ShiftUnits = shiftUnits,
            HeightUnits = heightUnits,
            DistortionUnits = distortionUnits,
            MaximumFieldDeg = maxField,
            MaximumDistortionPercent = maxDistortion,
            WavelengthData = wavelengthBlocks.ToArray()
        };

        void AddCurrentBlock()
        {
            if (fieldAngles.Count == 0) return;
            var expected = fieldAngles.Count;
            if (tanShifts.Count != expected || sagShifts.Count != expected || realHeights.Count != expected || refHeights.Count != expected || distortions.Count != expected)
                throw new InvalidDataException("Field Curvature and Distortion contained an incomplete data block.");
            wavelengthBlocks.Add(new FieldCurvatureWavelengthData
            {
                Wavelength = currentWavelength,
                DistortionFocalLength = currentFocalLength,
                FieldAnglesDeg = fieldAngles.ToArray(),
                TangentialShift = tanShifts.ToArray(),
                SagittalShift = sagShifts.ToArray(),
                RealHeight = realHeights.ToArray(),
                ReferenceHeight = refHeights.ToArray(),
                DistortionPercent = distortions.ToArray(),
                DataPoints = expected
            });
        }
    }

    private static string ParseUnits(string line)
    {
        var idx = line.IndexOf("are", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? line.Substring(idx + 3).Trim().TrimEnd('.') : "";
    }

    private static double ParseColonValue(string line)
    {
        int idx = line.LastIndexOf(':');
        if (idx < 0) return double.NaN;
        var part = line.Substring(idx + 1).Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return part.Length > 0 && double.TryParse(part[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }

    private static double ParseEqualsValue(string line)
    {
        int idx = line.LastIndexOf('=');
        if (idx < 0) return ParseColonValue(line);
        var part = line.Substring(idx + 1).Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return part.Length > 0 && double.TryParse(part[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }

    private static double ParseValueBeforeUnit(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var clean = parts[i].TrimEnd('.', '%');
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        }
        return double.NaN;
    }

    private static double ParseValueAfterEquals(string line)
    {
        int idx = line.IndexOf('=');
        if (idx < 0) return double.NaN;
        var part = line.Substring(idx + 1).Trim().TrimEnd('%').Trim();
        return double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }
}
