using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class LongitudinalAberrationTool
{
    private readonly IZemaxSession _session;

    public LongitudinalAberrationTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_longitudinal_aberration")]
    [Description("Calculate longitudinal aberration (focus shift) versus relative pupil for each wavelength. Returns the parsed aberration matrix and units from OpticStudio text output.")]
    public async Task<LongitudinalAberrationData> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("LongitudinalAberration", null, system =>
            {
                var analysis = system.Analyses.New_LongitudinalAberration();
                if (analysis == null)
                    throw new InvalidOperationException("OpticStudio did not create a Longitudinal Aberration analysis.");
                try
                {
                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Longitudinal Aberration returned no results object.");
                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_longab_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new IOException("Longitudinal Aberration produced no text results.");
                        return ParseLongitudinalTextFile(tempFile);
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
            return new LongitudinalAberrationData { Success = false, Error = ex.Message };
        }
    }

    private static LongitudinalAberrationData ParseLongitudinalTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        string units = "";
        double[]? wavelengths = null;
        var relPupils = new List<double>();
        var aberrationColumns = new List<List<double>>();
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

            if (trimmed.StartsWith("Rel.", StringComparison.OrdinalIgnoreCase) &&
                trimmed.IndexOf("Pupil", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                var waveList = new List<double>();
                for (int j = 2; j < parts.Length; j++)
                    if (double.TryParse(parts[j], NumberStyles.Float, CultureInfo.InvariantCulture, out var wave)) waveList.Add(wave);
                if (waveList.Count == 0)
                    throw new InvalidDataException("Longitudinal Aberration header contained no parsable wavelengths.");
                wavelengths = waveList.ToArray();
                aberrationColumns.Clear();
                for (int j = 0; j < wavelengths.Length; j++) aberrationColumns.Add(new List<double>());
                inData = true;
                continue;
            }

            if (!inData || wavelengths == null) continue;
            var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 1 + wavelengths.Length &&
                double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pupil))
            {
                var parsedRow = new double[wavelengths.Length];
                var rowValid = true;
                for (int j = 0; j < wavelengths.Length; j++)
                {
                    if (!double.TryParse(values[j + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out parsedRow[j]))
                    {
                        rowValid = false;
                        break;
                    }
                }
                if (!rowValid) continue;
                relPupils.Add(pupil);
                for (int j = 0; j < wavelengths.Length; j++) aberrationColumns[j].Add(parsedRow[j]);
            }
        }

        if (wavelengths == null || wavelengths.Length == 0 || relPupils.Count == 0 || aberrationColumns.Any(column => column.Count != relPupils.Count))
            throw new InvalidDataException("Longitudinal Aberration text results contained no complete parsable data matrix. The installed OpticStudio text format may be unsupported.");

        return new LongitudinalAberrationData
        {
            Success = true,
            Units = units,
            Wavelengths = wavelengths,
            RelativePupils = relPupils.ToArray(),
            Aberrations = aberrationColumns.Select(c => c.ToArray()).ToArray(),
            DataPoints = relPupils.Count
        };
    }
}
