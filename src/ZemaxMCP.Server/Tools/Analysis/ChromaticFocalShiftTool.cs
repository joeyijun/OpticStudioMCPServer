using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class ChromaticFocalShiftTool
{
    private readonly IZemaxSession _session;

    public ChromaticFocalShiftTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_chromatic_focal_shift")]
    [Description("Calculate chromatic focal shift (longitudinal chromatic aberration) versus wavelength. Returns the focal-shift curve and parsed range metadata when present in the installed OpticStudio text output.")]
    public async Task<ChromaticFocalShiftData> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("ChromaticFocalShift", null, system =>
            {
                var analysis = system.Analyses.New_FocalShiftDiagram();
                if (analysis == null)
                    throw new InvalidOperationException("OpticStudio did not create a Chromatic Focal Shift analysis.");
                try
                {
                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Chromatic Focal Shift returned no results object.");
                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_cfs_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new IOException("Chromatic Focal Shift produced no text results.");
                        return ParseChromaticFocalShiftTextFile(tempFile);
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
            return new ChromaticFocalShiftData { Success = false, Error = ex.Message };
        }
    }

    private static ChromaticFocalShiftData ParseChromaticFocalShiftTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        string wavelengthUnits = "", shiftUnits = "";
        double pupilZone = double.NaN, maxRange = double.NaN, dlRange = double.NaN;
        var wavelengths = new List<double>();
        var shifts = new List<double>();
        bool inData = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Wavelength units", StringComparison.OrdinalIgnoreCase))
            {
                var idx = trimmed.IndexOf("are", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) wavelengthUnits = trimmed.Substring(idx + 3).Trim().TrimEnd('.');
                continue;
            }
            if (trimmed.StartsWith("Shift units", StringComparison.OrdinalIgnoreCase))
            {
                var idx = trimmed.IndexOf("are", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) shiftUnits = trimmed.Substring(idx + 3).Trim().TrimEnd('.');
                continue;
            }
            if (trimmed.StartsWith("Pupil Zone", StringComparison.OrdinalIgnoreCase)) { pupilZone = ParseColonValue(trimmed); continue; }
            if (trimmed.StartsWith("Maximum Focal Shift", StringComparison.OrdinalIgnoreCase)) { maxRange = ParseColonValue(trimmed); continue; }
            if (trimmed.StartsWith("Diffraction Limited", StringComparison.OrdinalIgnoreCase)) { dlRange = ParseColonValue(trimmed); continue; }

            if (trimmed.IndexOf("Wavelength", StringComparison.OrdinalIgnoreCase) >= 0 &&
                trimmed.IndexOf("Shift", StringComparison.OrdinalIgnoreCase) >= 0 &&
                trimmed.IndexOf("units", StringComparison.OrdinalIgnoreCase) < 0 &&
                trimmed.IndexOf("Range", StringComparison.OrdinalIgnoreCase) < 0)
            {
                inData = true;
                continue;
            }
            if (!inData) continue;

            var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 2 &&
                double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var wl) &&
                double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var shift))
            {
                wavelengths.Add(wl);
                shifts.Add(shift);
            }
        }

        if (wavelengths.Count == 0 || wavelengths.Count != shifts.Count)
            throw new InvalidDataException("Chromatic Focal Shift text results contained no parsable wavelength/shift curve. The installed OpticStudio text format may be unsupported.");

        return new ChromaticFocalShiftData
        {
            Success = true,
            WavelengthUnits = wavelengthUnits,
            ShiftUnits = shiftUnits,
            PupilZone = pupilZone,
            MaximumFocalShiftRange = maxRange,
            DiffractionLimitedRange = dlRange,
            Wavelengths = wavelengths.ToArray(),
            Shifts = shifts.ToArray(),
            DataPoints = wavelengths.Count
        };
    }

    private static double ParseColonValue(string line)
    {
        int idx = line.LastIndexOf(':');
        if (idx < 0) return double.NaN;
        var part = line.Substring(idx + 1).Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return part.Length > 0 && double.TryParse(part[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
    }
}
