using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class DiffractionEncircledEnergyTool
{
    private readonly IZemaxSession _session;

    public DiffractionEncircledEnergyTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_diffraction_encircled_energy")]
    [Description("Calculate FFT diffraction encircled energy as a function of radial distance from the reference point. Returns the fraction of total energy enclosed within a given radius for each field point, plus the diffraction limit curve.")]
    public async Task<DiffractionEncircledEnergyData> ExecuteAsync(
        [Description("Sampling (1-6): 1=32x32, 2=64x64, 3=128x128, 4=256x256, 5=512x512, 6=1024x1024")] int sampling = 3,
        [Description("Use dashes for data instead of the reference-field center.")] bool useDashes = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (sampling is < 1 or > 6)
                throw new ArgumentOutOfRangeException(nameof(sampling), "Sampling must be in the range 1..6.");

            var parameters = new Dictionary<string, object?>
            {
                ["sampling"] = sampling,
                ["useDashes"] = useDashes
            };

            return await _session.ExecuteAsync("DiffractionEncircledEnergy", parameters, system =>
            {
                var analysis = system.Analyses.New_DiffractionEncircledEnergy();
                try
                {
                    var settings = analysis.GetSettings() as ZOSAPI.Analysis.Settings.EncircledEnergy.IAS_DiffractionEncircledEnergy
                        ?? throw new InvalidOperationException("OpticStudio did not expose Diffraction Encircled Energy settings.");
                    settings.SampleSize = MapSampling(sampling);
                    settings.UseDashes = useDashes;

                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Diffraction Encircled Energy returned no results object.");

                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_dee_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new InvalidOperationException("Diffraction Encircled Energy produced no text output.");
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
            return new DiffractionEncircledEnergyData { Success = false, Error = ex.Message };
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

    private static DiffractionEncircledEnergyData ParseTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        string surface = "", wavelength = "", reference = "", distanceUnits = "";
        var fields = new List<DiffractionEncircledEnergyFieldData>();
        string currentLabel = "";
        double currentFieldDeg = -1;
        double refX = 0, refY = 0;
        var distances = new List<double>();
        var fractions = new List<double>();
        bool inData = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Surface:", StringComparison.OrdinalIgnoreCase))
            {
                surface = trimmed.Substring("Surface:".Length).Trim();
                continue;
            }
            if (trimmed.StartsWith("Wavelength:", StringComparison.OrdinalIgnoreCase))
            {
                wavelength = trimmed.Substring("Wavelength:".Length).Trim();
                continue;
            }
            if (trimmed.StartsWith("Reference:", StringComparison.OrdinalIgnoreCase))
            {
                reference = trimmed.Substring("Reference:".Length).Trim();
                continue;
            }
            if (trimmed.StartsWith("Distance units", StringComparison.OrdinalIgnoreCase))
            {
                var idx = trimmed.IndexOf("are", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) distanceUnits = trimmed.Substring(idx + 3).Trim().TrimEnd('.');
                continue;
            }

            if (trimmed.StartsWith("Diff. Limit", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("Field:", StringComparison.OrdinalIgnoreCase))
            {
                SaveBlock(fields, currentLabel, currentFieldDeg, refX, refY, distances, fractions);
                distances = new List<double>();
                fractions = new List<double>();
                refX = 0;
                refY = 0;
                inData = false;

                if (trimmed.StartsWith("Diff. Limit", StringComparison.OrdinalIgnoreCase))
                {
                    currentLabel = "Diffraction Limit";
                    currentFieldDeg = -1;
                }
                else
                {
                    currentLabel = trimmed;
                    currentFieldDeg = ParseRequiredFieldValue(trimmed);
                }
                continue;
            }

            if (trimmed.StartsWith("Reference Coordinates:", StringComparison.OrdinalIgnoreCase))
            {
                ParseReferenceCoordinates(trimmed, out refX, out refY);
                continue;
            }

            var lower = trimmed.ToLowerInvariant();
            if (!inData && lower.Contains("radial distance") && lower.Contains("fraction"))
            {
                inData = true;
                continue;
            }
            if (!inData) continue;

            var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dist))
                continue;
            if (values.Length < 2 || !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double frac))
                throw new FormatException($"Could not parse Diffraction Encircled Energy row '{trimmed}'.");
            if (dist < 0 || frac < 0)
                throw new FormatException($"Diffraction Encircled Energy returned a negative distance/fraction in '{trimmed}'.");
            distances.Add(dist);
            fractions.Add(frac);
        }

        SaveBlock(fields, currentLabel, currentFieldDeg, refX, refY, distances, fractions);
        if (fields.Count == 0)
            throw new FormatException("Diffraction Encircled Energy text output contained no parseable data blocks.");

        return new DiffractionEncircledEnergyData
        {
            Success = true,
            Surface = surface,
            Wavelength = wavelength,
            Reference = reference,
            DistanceUnits = distanceUnits,
            Fields = fields.ToArray()
        };
    }

    private static void SaveBlock(List<DiffractionEncircledEnergyFieldData> fields, string label, double fieldValue,
        double refX, double refY, List<double> distances, List<double> fractions)
    {
        if (distances.Count == 0) return;
        if (string.IsNullOrWhiteSpace(label) || fractions.Count != distances.Count)
            throw new FormatException("Diffraction Encircled Energy block metadata/data dimensions are inconsistent.");
        fields.Add(new DiffractionEncircledEnergyFieldData
        {
            Label = label,
            FieldValueDeg = fieldValue,
            ReferenceX = refX,
            ReferenceY = refY,
            RadialDistances = distances.ToArray(),
            Fractions = fractions.ToArray(),
            DataPoints = distances.Count
        });
    }

    private static double ParseRequiredFieldValue(string line)
    {
        var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double val)) return val;
        throw new FormatException($"Could not parse encircled-energy field value from '{line}'.");
    }

    private static void ParseReferenceCoordinates(string line, out double x, out double y)
    {
        var upper = line.ToUpperInvariant();
        int xIdx = upper.IndexOf("X =", StringComparison.Ordinal);
        int yIdx = upper.IndexOf("Y =", StringComparison.Ordinal);
        if (xIdx < 0 || yIdx < 0 || yIdx <= xIdx)
            throw new FormatException($"Could not locate reference coordinates in '{line}'.");
        var xPart = line.Substring(xIdx + 3, yIdx - xIdx - 3).Trim();
        var yPart = line.Substring(yIdx + 3).Trim();
        if (!double.TryParse(xPart, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
            !double.TryParse(yPart, NumberStyles.Float, CultureInfo.InvariantCulture, out y))
            throw new FormatException($"Could not parse reference coordinates in '{line}'.");
    }
}
