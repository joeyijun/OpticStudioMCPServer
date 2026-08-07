using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;
using ZOSAPI.Analysis;
using ZOSAPI.Analysis.Settings.Mtf;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class MtfAnalysisTool
{
    private readonly IZemaxSession _session;

    public MtfAnalysisTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_fft_mtf")]
    [Description("Calculate FFT MTF for all fields. Returns tangential/sagittal curves for every field plus the diffraction limit up to the requested maximum spatial frequency.")]
    public async Task<MtfData> ExecuteAsync(
        [Description("Maximum spatial frequency in cycles/mm; must be finite and positive")] double frequency,
        [Description("Wavelength number (0 for polychromatic)")] int wavelength = 0,
        [Description("Sampling level 1-6: 32, 64, 128, 256, 512, or 1024 square samples")] int sampling = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (double.IsNaN(frequency) || double.IsInfinity(frequency) || frequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(frequency), "Maximum spatial frequency must be finite and positive.");
            if (wavelength < 0)
                throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be 0 (polychromatic) or a positive wavelength number.");
            if (sampling < 1 || sampling > 6)
                throw new ArgumentOutOfRangeException(nameof(sampling), "Sampling must be between 1 and 6.");

            var parameters = new Dictionary<string, object?>
            {
                ["frequency"] = frequency,
                ["wavelength"] = wavelength,
                ["sampling"] = sampling
            };

            return await _session.ExecuteAsync("MTF", parameters, system =>
            {
                var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                if (wavelength > wavelengthCount)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be 0 or between 1 and {wavelengthCount}.");

                var fftMtfAnalysis = system.Analyses.New_FftMtf();
                if (fftMtfAnalysis == null)
                    throw new InvalidOperationException("OpticStudio did not create an FFT MTF analysis.");
                try
                {
                    if (fftMtfAnalysis.GetSettings() is not IAS_FftMtf settings)
                        throw new InvalidOperationException("OpticStudio did not expose FFT MTF settings through IAS_FftMtf.");

                    settings.Type = MtfTypes.Modulation;
                    settings.Field.UseAllFields();
                    settings.Wavelength.SetWavelengthNumber(wavelength);
                    settings.SampleSize = MapSampling(sampling);
                    settings.MaximumFrequency = frequency;
                    settings.ShowDiffractionLimit = true;

                    fftMtfAnalysis.ApplyAndWaitForCompletion();

                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_fft_mtf_{Guid.NewGuid():N}.txt");
                    try
                    {
                        fftMtfAnalysis.GetResults().GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new IOException("FFT MTF analysis produced no text results.");
                        return ParseMtfTextFile(tempFile, frequency, wavelength);
                    }
                    finally
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }
                finally
                {
                    fftMtfAnalysis.Close();
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new MtfData
            {
                Success = false,
                Error = ex.Message,
                MaxFrequency = frequency,
                Wavelength = wavelength
            };
        }
    }

    private static SampleSizes MapSampling(int sampling) => sampling switch
    {
        1 => SampleSizes.S_32x32,
        2 => SampleSizes.S_64x64,
        3 => SampleSizes.S_128x128,
        4 => SampleSizes.S_256x256,
        5 => SampleSizes.S_512x512,
        6 => SampleSizes.S_1024x1024,
        _ => throw new ArgumentOutOfRangeException(nameof(sampling))
    };

    private static MtfData ParseMtfTextFile(string filePath, double maxFreq, int wavelength)
    {
        var lines = File.ReadAllLines(filePath);
        var sections = new List<(string label, List<double> freqs, List<double> tan, List<double> sag)>();
        string? currentLabel = null;
        List<double>? curFreqs = null;
        List<double>? curTan = null;
        List<double>? curSag = null;
        var inData = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Field:", StringComparison.OrdinalIgnoreCase) &&
                trimmed.IndexOf("Field type", StringComparison.OrdinalIgnoreCase) < 0)
            {
                if (currentLabel != null && curFreqs is { Count: > 0 })
                    sections.Add((currentLabel, curFreqs, curTan!, curSag!));
                currentLabel = trimmed.Substring(6).Trim();
                curFreqs = new List<double>();
                curTan = new List<double>();
                curSag = new List<double>();
                inData = false;
                continue;
            }

            var lower = trimmed.ToLowerInvariant();
            if (!inData && currentLabel != null && lower.Contains("freq") && (lower.Contains("tan") || lower.Contains("sag")))
            {
                inData = true;
                continue;
            }
            if (!inData || curFreqs == null) continue;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                if (curFreqs.Count > 0) inData = false;
                continue;
            }

            var values = trimmed.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length >= 3 && double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var freq))
            {
                curFreqs.Add(freq);
                AddParsedValue(values, 1, curTan!);
                AddParsedValue(values, 2, curSag!);
            }
        }

        if (currentLabel != null && curFreqs is { Count: > 0 })
            sections.Add((currentLabel, curFreqs, curTan!, curSag!));
        if (sections.Count == 0)
            throw new InvalidDataException("FFT MTF text results contained no parsable field data. The installed OpticStudio text format may be unsupported.");

        (string label, List<double> freqs, List<double> tan, List<double> sag)? dlSection = null;
        var fieldSections = new List<(string label, List<double> freqs, List<double> tan, List<double> sag)>();
        foreach (var section in sections)
        {
            if (section.label.IndexOf("Diffraction", StringComparison.OrdinalIgnoreCase) >= 0) dlSection = section;
            else fieldSections.Add(section);
        }
        if (fieldSections.Count == 0)
            throw new InvalidDataException("FFT MTF text results contained no field sections.");

        var fields = new MtfFieldData[fieldSections.Count];
        for (var i = 0; i < fieldSections.Count; i++)
        {
            var fs = fieldSections[i];
            fields[i] = new MtfFieldData
            {
                FieldLabel = fs.label,
                FieldNumber = i + 1,
                Frequencies = fs.freqs.ToArray(),
                TangentialMtf = fs.tan.ToArray(),
                SagittalMtf = fs.sag.ToArray(),
                DataPoints = fs.freqs.Count
            };
        }

        return new MtfData
        {
            Success = true,
            Fields = fields,
            DiffractionLimitFrequencies = dlSection?.freqs.ToArray(),
            DiffractionLimitTangential = dlSection?.tan.ToArray(),
            DiffractionLimitSagittal = dlSection?.sag.ToArray(),
            MaxFrequency = maxFreq,
            Wavelength = wavelength,
            TotalFields = fieldSections.Count,
            DataPoints = fieldSections[0].freqs.Count
        };
    }

    private static void AddParsedValue(string[] values, int index, List<double> list)
    {
        if (index < values.Length && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            list.Add(value);
        else
            list.Add(double.NaN);
    }
}
