using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class PupilAberrationFanTool
{
    private readonly IZemaxSession _session;

    public PupilAberrationFanTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_pupil_aberration_fan")]
    [Description("Calculate entrance pupil aberration fan for all fields and wavelengths. Returns tangential and sagittal fans showing the deviation of the actual ray entrance pupil coordinates from ideal (paraxial) coordinates. Units are percent.")]
    public async Task<RayFanData> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("PupilAberrationFan", null, system =>
            {
                var analysis = system.Analyses.New_PupilAberrationFan();
                try
                {
                    analysis.ApplyAndWaitForCompletion();
                    var results = analysis.GetResults() ?? throw new InvalidOperationException("Pupil Aberration Fan returned no results object.");

                    var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_pupilab_{Guid.NewGuid():N}.txt");
                    try
                    {
                        results.GetTextFile(tempFile);
                        if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                            throw new InvalidOperationException("Pupil Aberration Fan produced no text output.");
                        return ParsePupilAberrationTextFile(tempFile);
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
            return new RayFanData { Success = false, Error = ex.Message };
        }
    }

    private static RayFanData ParsePupilAberrationTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        string units = "";
        string surface = "";
        var fields = new List<RayFanFieldData>();
        int currentFieldNumber = 0;
        double currentFieldValue = double.NaN;
        string currentFanType = "";
        double[]? wavelengths = null;
        var pupils = new List<double>();
        var aberrationColumns = new List<List<double>>();
        bool inData = false;
        RayFanCurveData? currentTangential = null;
        RayFanCurveData? currentSagittal = null;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            if (trimmed.StartsWith("Surface:", StringComparison.OrdinalIgnoreCase))
            {
                surface = trimmed.Substring(8).Trim();
                continue;
            }
            if (trimmed.StartsWith("Units are", StringComparison.OrdinalIgnoreCase))
            {
                var idx = trimmed.IndexOf("are", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) units = trimmed.Substring(idx + 3).Trim().TrimEnd('.');
                continue;
            }

            if (trimmed.IndexOf("fan, field number", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                SaveCurrentFan(ref currentTangential, ref currentSagittal, currentFanType, wavelengths, pupils, aberrationColumns);
                bool isTangential = trimmed.StartsWith("Tangential", StringComparison.OrdinalIgnoreCase);
                bool isSagittal = trimmed.StartsWith("Sagittal", StringComparison.OrdinalIgnoreCase);
                if (!isTangential && !isSagittal)
                    throw new FormatException($"Unrecognized Pupil Aberration Fan section header: '{trimmed}'.");

                int newFieldNumber = ParseRequiredFieldNumber(trimmed);
                double newFieldValue = ParseRequiredFieldValue(trimmed);
                if (isTangential && currentFieldNumber > 0 && currentFieldNumber != newFieldNumber)
                {
                    AddCompletedField(fields, currentFieldNumber, currentFieldValue, currentTangential, currentSagittal);
                    currentTangential = null;
                    currentSagittal = null;
                }

                if (isTangential)
                {
                    currentFieldNumber = newFieldNumber;
                    currentFieldValue = newFieldValue;
                    currentFanType = "Tangential";
                }
                else
                {
                    if (currentFieldNumber == 0 || newFieldNumber != currentFieldNumber)
                        throw new FormatException("Sagittal Pupil Aberration Fan section does not match the active field.");
                    currentFanType = "Sagittal";
                }

                wavelengths = null;
                pupils = new List<double>();
                aberrationColumns = new List<List<double>>();
                inData = false;
                continue;
            }

            if (trimmed.StartsWith("Pupil", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                var waveList = new List<double>();
                for (int j = 1; j < parts.Length; j++)
                {
                    if (!double.TryParse(parts[j], NumberStyles.Float, CultureInfo.InvariantCulture, out double w))
                        throw new FormatException($"Could not parse Pupil Aberration Fan wavelength '{parts[j]}'.");
                    waveList.Add(w);
                }
                if (waveList.Count == 0)
                    throw new FormatException("Pupil Aberration Fan data header contained no wavelengths.");
                wavelengths = waveList.ToArray();
                aberrationColumns = wavelengths.Select(_ => new List<double>()).ToList();
                inData = true;
                continue;
            }

            if (!inData || wavelengths == null) continue;
            var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (!double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pupil))
                continue;
            if (values.Length < 1 + wavelengths.Length)
                throw new FormatException("Pupil Aberration Fan data row has fewer values than the wavelength header.");

            pupils.Add(pupil);
            for (int j = 0; j < wavelengths.Length; j++)
            {
                if (!double.TryParse(values[j + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ab))
                    throw new FormatException($"Could not parse Pupil Aberration Fan value '{values[j + 1]}'.");
                aberrationColumns[j].Add(ab);
            }
        }

        SaveCurrentFan(ref currentTangential, ref currentSagittal, currentFanType, wavelengths, pupils, aberrationColumns);
        if (currentFieldNumber > 0)
            AddCompletedField(fields, currentFieldNumber, currentFieldValue, currentTangential, currentSagittal);
        if (fields.Count == 0)
            throw new FormatException("Pupil Aberration Fan text output contained no parseable field data.");

        return new RayFanData { Success = true, Units = units, Surface = surface, Fields = fields.ToArray() };
    }

    private static void AddCompletedField(List<RayFanFieldData> fields, int fieldNumber, double fieldValue,
        RayFanCurveData? tangential, RayFanCurveData? sagittal)
    {
        if (tangential == null || sagittal == null)
            throw new FormatException($"Pupil Aberration Fan field {fieldNumber} did not contain both tangential and sagittal data.");
        fields.Add(new RayFanFieldData
        {
            FieldNumber = fieldNumber,
            FieldValueDeg = fieldValue,
            Tangential = tangential,
            Sagittal = sagittal
        });
    }

    private static void SaveCurrentFan(ref RayFanCurveData? tangential, ref RayFanCurveData? sagittal,
        string fanType, double[]? wavelengths, List<double> pupils, List<List<double>> aberrationColumns)
    {
        if (wavelengths == null || pupils.Count == 0) return;
        if (aberrationColumns.Count != wavelengths.Length || aberrationColumns.Any(c => c.Count != pupils.Count))
            throw new FormatException("Pupil Aberration Fan data columns are inconsistent with the pupil/wavelength dimensions.");

        var curve = new RayFanCurveData
        {
            Wavelengths = wavelengths,
            PupilCoordinates = pupils.ToArray(),
            Aberration = aberrationColumns.Select(c => c.ToArray()).ToArray(),
            DataPoints = pupils.Count
        };
        if (fanType == "Tangential") tangential = curve;
        else if (fanType == "Sagittal") sagittal = curve;
        else throw new FormatException("Pupil Aberration Fan data appeared before a tangential/sagittal section header.");
    }

    private static int ParseRequiredFieldNumber(string line)
    {
        var idx = line.IndexOf("number", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) throw new FormatException($"Pupil Aberration Fan field number is missing: '{line}'.");
        var parts = line.Substring(idx + 6).Trim().Split([' ', '='], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int num) || num <= 0)
            throw new FormatException($"Could not parse Pupil Aberration Fan field number from '{line}'.");
        return num;
    }

    private static double ParseRequiredFieldValue(string line)
    {
        var idx = line.IndexOf('=');
        if (idx < 0) throw new FormatException($"Pupil Aberration Fan field value is missing: '{line}'.");
        var parts = line.Substring(idx + 1).Trim().Split([' ', '\t', '('], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
            throw new FormatException($"Could not parse Pupil Aberration Fan field value from '{line}'.");
        return val;
    }
}
