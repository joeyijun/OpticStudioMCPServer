using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class FftMtfVsFieldTool
{
    private readonly IZemaxSession _session;

    public FftMtfVsFieldTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_fft_mtf_vs_field")]
    [Description("Calculate polychromatic FFT MTF as a function of field for up to 6 spatial frequencies. Returns tangential and sagittal modulation versus relative field for each requested frequency.")]
    public async Task<FftMtfVsFieldData> ExecuteAsync(
        [Description("Spatial frequency 1 in cycles/mm; must be finite and positive")] double frequency1 = 10,
        [Description("Spatial frequency 2 in cycles/mm (0 to skip)")] double frequency2 = 0,
        [Description("Spatial frequency 3 in cycles/mm (0 to skip)")] double frequency3 = 0,
        [Description("Spatial frequency 4 in cycles/mm (0 to skip)")] double frequency4 = 0,
        [Description("Spatial frequency 5 in cycles/mm (0 to skip)")] double frequency5 = 0,
        [Description("Spatial frequency 6 in cycles/mm (0 to skip)")] double frequency6 = 0,
        [Description("Sampling level 1-6: 32, 64, 128, 256, 512, or 1024 square samples")] int sampling = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var frequencies = new[] { frequency1, frequency2, frequency3, frequency4, frequency5, frequency6 };
            if (frequencies.Any(value => double.IsNaN(value) || double.IsInfinity(value)))
                throw new ArgumentException("All spatial frequencies must be finite.");
            if (frequency1 <= 0)
                throw new ArgumentOutOfRangeException(nameof(frequency1), "The first spatial frequency must be positive.");
            if (frequencies.Skip(1).Any(value => value < 0))
                throw new ArgumentOutOfRangeException(nameof(frequency2), "Optional spatial frequencies must be zero (skip) or positive.");
            var requested = frequencies.Where(value => value > 0).ToArray();
            if (requested.Distinct().Count() != requested.Length)
                throw new ArgumentException("Requested non-zero spatial frequencies must be unique.");
            if (sampling < 1 || sampling > 6)
                throw new ArgumentOutOfRangeException(nameof(sampling), "Sampling must be between 1 and 6.");

            var parameters = new Dictionary<string, object?>
            {
                ["frequency1"] = frequency1,
                ["frequency2"] = frequency2,
                ["frequency3"] = frequency3,
                ["frequency4"] = frequency4,
                ["frequency5"] = frequency5,
                ["frequency6"] = frequency6,
                ["sampling"] = sampling
            };

            return await _session.ExecuteAsync("FftMtfVsField", parameters, system =>
            {
                var analysis = system.Analyses.New_FftMtfvsField();
                if (analysis == null)
                    throw new InvalidOperationException("OpticStudio did not create an FFT MTF vs Field analysis.");
                try
                {
                    if (analysis.GetSettings() is not ZOSAPI.Analysis.Settings.Mtf.IAS_FftMtfvsField settings)
                        throw new InvalidOperationException("OpticStudio did not expose FFT MTF vs Field settings through IAS_FftMtfvsField.");

                    settings.SampleSize = MapSampling(sampling);
                    settings.Freq_1 = frequency1;
                    settings.Freq_2 = frequency2;
                    settings.Freq_3 = frequency3;
                    settings.Freq_4 = frequency4;
                    settings.Freq_5 = frequency5;
                    settings.Freq_6 = frequency6;

                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("FFT MTF vs Field returned no results object.");

                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_fftmtfvsfield_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new IOException("FFT MTF vs Field produced no text results.");
                        return ParseTextFile(tempFile, requested);
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

    private static ZOSAPI.Analysis.SampleSizes MapSampling(int sampling) => sampling switch
    {
        1 => ZOSAPI.Analysis.SampleSizes.S_32x32,
        2 => ZOSAPI.Analysis.SampleSizes.S_64x64,
        3 => ZOSAPI.Analysis.SampleSizes.S_128x128,
        4 => ZOSAPI.Analysis.SampleSizes.S_256x256,
        5 => ZOSAPI.Analysis.SampleSizes.S_512x512,
        6 => ZOSAPI.Analysis.SampleSizes.S_1024x1024,
        _ => throw new ArgumentOutOfRangeException(nameof(sampling))
    };

    private static FftMtfVsFieldData ParseTextFile(string filePath, IReadOnlyCollection<double> requestedFrequencies)
    {
        var lines = File.ReadAllLines(filePath);

        double maxField = 0;
        string wavelengthRange = "";
        var freqBlocks = new List<FftMtfVsFieldFrequencyData>();

        double currentFreq = 0;
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
                maxField = ParseValueFromLine(trimmed);
                continue;
            }

            if (trimmed.StartsWith("Data for", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("µm"))
            {
                wavelengthRange = trimmed;
                continue;
            }

            if (trimmed.StartsWith("Data for spatial frequency", StringComparison.OrdinalIgnoreCase))
            {
                AddCurrentBlock();
                currentFreq = ParseValueAfterColon(trimmed);
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
            if (values.Length >= 3 &&
                double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rf) &&
                double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tan) &&
                double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sag))
            {
                relFields.Add(rf);
                tanValues.Add(tan);
                sagValues.Add(sag);
            }
        }
        AddCurrentBlock();

        if (freqBlocks.Count == 0)
            throw new InvalidDataException("FFT MTF vs Field text results contained no parsable frequency blocks. The installed OpticStudio text format may be unsupported.");
        if (freqBlocks.Count < requestedFrequencies.Count)
            throw new InvalidDataException($"FFT MTF vs Field returned {freqBlocks.Count} parsable frequency blocks for {requestedFrequencies.Count} requested non-zero frequencies.");

        return new FftMtfVsFieldData
        {
            Success = true,
            MaximumFieldDeg = maxField,
            WavelengthRange = wavelengthRange,
            FrequencyData = freqBlocks.ToArray()
        };

        void AddCurrentBlock()
        {
            if (relFields.Count == 0) return;
            freqBlocks.Add(new FftMtfVsFieldFrequencyData
            {
                SpatialFrequency = currentFreq,
                RelativeFields = relFields.ToArray(),
                Tangential = tanValues.ToArray(),
                Sagittal = sagValues.ToArray(),
                DataPoints = relFields.Count
            });
        }
    }

    private static double ParseValueAfterColon(string line)
    {
        int idx = line.LastIndexOf(':');
        if (idx < 0) throw new InvalidDataException("Spatial-frequency header did not contain a colon.");
        var parts = line.Substring(idx + 1).Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
            return val;
        throw new InvalidDataException("Spatial-frequency header did not contain a parsable numeric value.");
    }

    private static double ParseValueFromLine(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            var clean = parts[i].TrimEnd('.', '%');
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                return val;
        }
        throw new InvalidDataException("Maximum-field header did not contain a parsable numeric value.");
    }
}
