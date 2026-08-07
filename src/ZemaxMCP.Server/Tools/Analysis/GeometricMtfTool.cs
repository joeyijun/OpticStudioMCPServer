using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class GeometricMtfTool
{
    private readonly IZemaxSession _session;

    public GeometricMtfTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_geometric_mtf")]
    [Description("Calculate Geometric (ray-based) MTF for all fields at once. Returns tangential and sagittal curves for every field up to the requested maximum spatial frequency.")]
    public async Task<MtfData> ExecuteAsync(
        [Description("Maximum spatial frequency in cycles/mm; must be finite and positive")] double maxFrequency = 100,
        [Description("Wavelength number (0 for polychromatic)")] int wavelength = 0,
        [Description("Multiply by diffraction limit")] bool multiplyByDiffractionLimit = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (double.IsNaN(maxFrequency) || double.IsInfinity(maxFrequency) || maxFrequency <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxFrequency), "Maximum spatial frequency must be finite and positive.");
            if (wavelength < 0)
                throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be 0 (polychromatic) or a positive wavelength number.");

            var parameters = new Dictionary<string, object?>
            {
                ["maxFrequency"] = maxFrequency,
                ["wavelength"] = wavelength,
                ["multiplyByDiffractionLimit"] = multiplyByDiffractionLimit
            };

            return await _session.ExecuteAsync("GeometricMTF", parameters, system =>
            {
                var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                if (wavelength > wavelengthCount)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be 0 or between 1 and {wavelengthCount}.");

                var geoMtf = system.Analyses.New_GeometricMtf();
                if (geoMtf == null)
                    throw new InvalidOperationException("OpticStudio did not create a Geometric MTF analysis.");
                try
                {
                    if (geoMtf.GetSettings() is not ZOSAPI.Analysis.Settings.Mtf.IAS_GeometricMtf settings)
                        throw new InvalidOperationException("OpticStudio did not expose Geometric MTF settings through IAS_GeometricMtf.");

                    settings.MultiplyByDiffractionLimit = multiplyByDiffractionLimit;
                    settings.MaximumFrequency = maxFrequency;
                    settings.Wavelength.SetWavelengthNumber(wavelength);

                    geoMtf.ApplyAndWaitForCompletion();
                    var results = geoMtf.GetResults() ?? throw new InvalidOperationException("Geometric MTF returned no results object.");
                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_geomtf_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new IOException("Geometric MTF produced no text results.");
                        return ParseGeometricMtfTextFile(tempFile, maxFrequency, wavelength);
                    }
                    finally
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }
                finally
                {
                    geoMtf.Close();
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new MtfData
            {
                Success = false,
                Error = ex.Message,
                MaxFrequency = maxFrequency,
                Wavelength = wavelength
            };
        }
    }

    private static MtfData ParseGeometricMtfTextFile(string filePath, double maxFreq, int wavelength)
    {
        var lines = File.ReadAllLines(filePath);
        var sections = new List<(string label, List<double> freqs, List<double> tan, List<double> sag)>();
        string? currentLabel = null;
        List<double>? curFreqs = null;
        List<double>? curTan = null;
        List<double>? curSag = null;
        bool inData = false;

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
            throw new InvalidDataException("Geometric MTF text results contained no parsable data sections. The installed OpticStudio text format may be unsupported.");

        (string label, List<double> freqs, List<double> tan, List<double> sag)? dlSection = null;
        var fieldSections = new List<(string label, List<double> freqs, List<double> tan, List<double> sag)>();
        foreach (var section in sections)
        {
            if (section.label.IndexOf("Diffraction", StringComparison.OrdinalIgnoreCase) >= 0) dlSection = section;
            else fieldSections.Add(section);
        }
        if (fieldSections.Count == 0)
            throw new InvalidDataException("Geometric MTF text results contained no field sections.");

        var fields = new MtfFieldData[fieldSections.Count];
        for (int i = 0; i < fieldSections.Count; i++)
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
