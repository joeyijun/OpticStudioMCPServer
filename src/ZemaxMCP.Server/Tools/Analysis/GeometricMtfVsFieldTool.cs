using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class GeometricMtfVsFieldTool
{
    private readonly IZemaxSession _session;

    public GeometricMtfVsFieldTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_geometric_mtf_vs_field")]
    [Description("Calculate polychromatic Geometric MTF as a function of field for up to 6 spatial frequencies. Frequency values use the system's current MTF units. Returns tangential and sagittal modulation versus relative field for each requested frequency.")]
    public async Task<FftMtfVsFieldData> ExecuteAsync(
        [Description("Spatial frequency 1 in the current system MTF units; must be > 0.")] double frequency1 = 10,
        [Description("Spatial frequency 2 in current MTF units (0 to skip).")] double frequency2 = 0,
        [Description("Spatial frequency 3 in current MTF units (0 to skip).")] double frequency3 = 0,
        [Description("Spatial frequency 4 in current MTF units (0 to skip).")] double frequency4 = 0,
        [Description("Spatial frequency 5 in current MTF units (0 to skip).")] double frequency5 = 0,
        [Description("Spatial frequency 6 in current MTF units (0 to skip).")] double frequency6 = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var frequencies = new[] { frequency1, frequency2, frequency3, frequency4, frequency5, frequency6 };
            if (frequencies.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new ArgumentOutOfRangeException(nameof(frequency1), "All spatial frequencies must be finite.");
            if (frequency1 <= 0)
                throw new ArgumentOutOfRangeException(nameof(frequency1), "frequency1 must be > 0.");
            if (frequencies.Skip(1).Any(value => value < 0))
                throw new ArgumentOutOfRangeException(nameof(frequency2), "Optional spatial frequencies must be >= 0 (0 means skip).");

            var parameters = new Dictionary<string, object?>
            {
                ["frequency1"] = frequency1,
                ["frequency2"] = frequency2,
                ["frequency3"] = frequency3,
                ["frequency4"] = frequency4,
                ["frequency5"] = frequency5,
                ["frequency6"] = frequency6
            };

            return await _session.ExecuteAsync("GeometricMtfVsField", parameters, system =>
            {
                var analysis = system.Analyses.New_GeometricMtfvsField();
                try
                {
                    var settings = analysis.GetSettings() as ZOSAPI.Analysis.Settings.Mtf.IAS_GeometricMtfvsField
                        ?? throw new InvalidOperationException("OpticStudio did not expose Geometric MTF vs Field settings.");
                    settings.Freq_1 = frequency1;
                    settings.Freq_2 = frequency2;
                    settings.Freq_3 = frequency3;
                    settings.Freq_4 = frequency4;
                    settings.Freq_5 = frequency5;
                    settings.Freq_6 = frequency6;

                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Geometric MTF vs Field returned no results object.");

                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_geomtfvsfield_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new InvalidOperationException("Geometric MTF vs Field produced no text output.");
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
            return new FftMtfVsFieldData { Success = false, Error = ex.Message };
        }
    }

    private static FftMtfVsFieldData ParseTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        double maxField = 0;
        string wavelengthRange = "";
        var freqBlocks = new List<FftMtfVsFieldFrequencyData>();
        double currentFreq = double.NaN;
        var relFields = new List<double>();
        var tanValues = new List<double>();
        var sagValues = new List<double>();
        bool inData = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Maximum Y field", StringComparison.OrdinalIgnoreCase))
            {
                maxField = ParseRequiredValueFromLine(trimmed, "maximum field");
                continue;
            }
            if (trimmed.StartsWith("Data for", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("µm"))
            {
                wavelengthRange = trimmed;
                continue;
            }
            if (trimmed.StartsWith("Data for spatial frequency", StringComparison.OrdinalIgnoreCase))
            {
                SaveBlock(freqBlocks, currentFreq, relFields, tanValues, sagValues);
                currentFreq = ParseRequiredValueAfterColon(trimmed, "spatial frequency");
                relFields = new List<double>();
                tanValues = new List<double>();
                sagValues = new List<double>();
                inData = false;
                continue;
            }

            var lower = trimmed.ToLowerInvariant();
            if (!inData && lower.Contains("relative field") && lower.Contains("tangential") && lower.Contains("sagittal"))
            {
                inData = true;
                continue;
            }
            if (!inData) continue;

            var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rf))
                continue;
            if (values.Length < 3 ||
                !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double tan) ||
                !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double sag))
                throw new FormatException($"Could not parse Geometric MTF vs Field data row '{trimmed}'.");
            relFields.Add(rf);
            tanValues.Add(tan);
            sagValues.Add(sag);
        }

        SaveBlock(freqBlocks, currentFreq, relFields, tanValues, sagValues);
        if (freqBlocks.Count == 0)
            throw new FormatException("Geometric MTF vs Field text output contained no parseable frequency blocks.");

        return new FftMtfVsFieldData
        {
            Success = true,
            MaximumFieldDeg = maxField,
            WavelengthRange = wavelengthRange,
            FrequencyData = freqBlocks.ToArray()
        };
    }

    private static void SaveBlock(List<FftMtfVsFieldFrequencyData> blocks, double frequency,
        List<double> relativeFields, List<double> tangential, List<double> sagittal)
    {
        if (relativeFields.Count == 0) return;
        if (double.IsNaN(frequency) || tangential.Count != relativeFields.Count || sagittal.Count != relativeFields.Count)
            throw new FormatException("Geometric MTF vs Field block metadata/data dimensions are inconsistent.");
        blocks.Add(new FftMtfVsFieldFrequencyData
        {
            SpatialFrequency = frequency,
            RelativeFields = relativeFields.ToArray(),
            Tangential = tangential.ToArray(),
            Sagittal = sagittal.ToArray(),
            DataPoints = relativeFields.Count
        });
    }

    private static double ParseRequiredValueAfterColon(string line, string label)
    {
        int idx = line.LastIndexOf(':');
        if (idx < 0) throw new FormatException($"Could not locate {label} in '{line}'.");
        var parts = line.Substring(idx + 1).Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new FormatException($"Could not parse {label} from '{line}'.");
        return value;
    }

    private static double ParseRequiredValueFromLine(string line, string label)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var clean = parts[i].TrimEnd('.', '%');
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return value;
        }
        throw new FormatException($"Could not parse {label} from '{line}'.");
    }
}
