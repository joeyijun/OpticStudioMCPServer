using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class RelativeIlluminationTool
{
    private readonly IZemaxSession _session;

    public RelativeIlluminationTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_relative_illumination")]
    [Description("Calculate relative illumination as a function of field angle. Shows how image illumination falls off from the center to the edge of the field, including the effective F/# at each field point.")]
    public async Task<RelativeIlluminationData> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("RelativeIllumination", null, system =>
            {
                var analysis = system.Analyses.New_RelativeIllumination();
                try
                {
                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Relative Illumination returned no results object.");

                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_ri_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new InvalidOperationException("Relative Illumination produced no text output.");
                        return ParseRelativeIlluminationTextFile(tempFile);
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
            return new RelativeIlluminationData { Success = false, Error = ex.Message };
        }
    }

    private static RelativeIlluminationData ParseRelativeIlluminationTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        double wavelength = double.NaN;
        string fieldUnits = "";
        var fields = new List<double>();
        var relIll = new List<double>();
        var effFNum = new List<double>();
        bool inData = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Wavelength:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out wavelength))
                    throw new FormatException($"Could not parse Relative Illumination wavelength from '{trimmed}'.");
                continue;
            }

            if (trimmed.StartsWith("Field values are in", StringComparison.OrdinalIgnoreCase))
            {
                const string prefix = "Field values are in";
                var idx = trimmed.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) fieldUnits = trimmed.Substring(idx + prefix.Length).Trim().TrimEnd('.');
                continue;
            }

            if (trimmed.IndexOf("Field", StringComparison.OrdinalIgnoreCase) >= 0 &&
                trimmed.IndexOf("Rel. Ill", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                inData = true;
                continue;
            }

            if (!inData) continue;
            var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double field))
                continue;
            if (values.Length < 3 ||
                !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ri) ||
                !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double efn))
                throw new FormatException($"Could not parse Relative Illumination data row '{trimmed}'.");

            fields.Add(field);
            relIll.Add(ri);
            effFNum.Add(efn);
        }

        if (fields.Count == 0)
            throw new FormatException("Relative Illumination text output contained no parseable data rows.");
        if (relIll.Count != fields.Count || effFNum.Count != fields.Count)
            throw new FormatException("Relative Illumination data columns have inconsistent lengths.");

        return new RelativeIlluminationData
        {
            Success = true,
            Wavelength = wavelength,
            FieldUnits = fieldUnits,
            FieldValues = fields.ToArray(),
            RelativeIllumination = relIll.ToArray(),
            EffectiveFNumber = effFNum.ToArray(),
            DataPoints = fields.Count
        };
    }
}
