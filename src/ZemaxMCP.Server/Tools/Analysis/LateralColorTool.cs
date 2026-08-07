using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class LateralColorTool
{
    private readonly IZemaxSession _session;

    public LateralColorTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_lateral_color")]
    [Description("Calculate lateral color versus relative field using OpticStudio's short/long wavelength comparison. Returns parsed field/color data and wavelength metadata when present.")]
    public async Task<LateralColorData> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("LateralColor", null, system =>
            {
                var analysis = system.Analyses.New_LateralColor();
                if (analysis == null)
                    throw new InvalidOperationException("OpticStudio did not create a Lateral Color analysis.");
                try
                {
                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Lateral Color returned no results object.");
                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_latcolor_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new IOException("Lateral Color produced no text results.");
                        return ParseLateralColorTextFile(tempFile);
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
            return new LateralColorData { Success = false, Error = ex.Message };
        }
    }

    private static LateralColorData ParseLateralColorTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        string units = "";
        double maxField = double.NaN, shortWave = double.NaN, longWave = double.NaN;
        var relFields = new List<double>();
        var latColor = new List<double>();
        bool inData = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Units are", StringComparison.OrdinalIgnoreCase))
            {
                var idx = trimmed.IndexOf("are", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) units = trimmed.Substring(idx + 3).Trim().TrimEnd('.');
                continue;
            }
            if (trimmed.StartsWith("Maximum Field", StringComparison.OrdinalIgnoreCase)) { maxField = ParseColonValue(trimmed); continue; }
            if (trimmed.StartsWith("Short Wavelength", StringComparison.OrdinalIgnoreCase)) { shortWave = ParseColonValue(trimmed); continue; }
            if (trimmed.StartsWith("Long Wavelength", StringComparison.OrdinalIgnoreCase)) { longWave = ParseColonValue(trimmed); continue; }

            if (trimmed.StartsWith("Rel.", StringComparison.OrdinalIgnoreCase) &&
                trimmed.IndexOf("Lateral Color", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                inData = true;
                continue;
            }
            if (!inData) continue;

            var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 2 &&
                double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rf) &&
                double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lc))
            {
                relFields.Add(rf);
                latColor.Add(lc);
            }
        }

        // Some OpticStudio versions label both wavelength lines similarly. If
        // the explicit long-wave header was not parsed, recover the first two
        // positive wavelength values from generic wavelength metadata lines.
        if (double.IsNaN(longWave))
        {
            var parsedWaves = new List<double>();
            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (trimmed.IndexOf("Wavelength", StringComparison.OrdinalIgnoreCase) < 0 ||
                    !trimmed.Contains(':') || trimmed.StartsWith("Maximum", StringComparison.OrdinalIgnoreCase)) continue;
                var value = ParseColonValue(trimmed);
                if (!double.IsNaN(value) && value > 0) parsedWaves.Add(value);
            }
            if (parsedWaves.Count >= 2)
            {
                shortWave = parsedWaves[0];
                longWave = parsedWaves[1];
            }
        }

        if (relFields.Count == 0 || relFields.Count != latColor.Count)
            throw new InvalidDataException("Lateral Color text results contained no complete parsable field/color curve. The installed OpticStudio text format may be unsupported.");

        return new LateralColorData
        {
            Success = true,
            Units = units,
            MaximumFieldDeg = maxField,
            ShortWavelength = shortWave,
            LongWavelength = longWave,
            RelativeFields = relFields.ToArray(),
            LateralColor = latColor.ToArray(),
            DataPoints = relFields.Count
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
