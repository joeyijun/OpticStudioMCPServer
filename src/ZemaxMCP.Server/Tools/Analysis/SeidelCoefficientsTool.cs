using System.ComponentModel;
using System.Globalization;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class SeidelCoefficientsTool
{
    private readonly IZemaxSession _session;

    public SeidelCoefficientsTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_seidel_coefficients")]
    [Description("Get Seidel (3rd-order) aberration coefficients for each surface and the total system, plus the wavefront summary when present in the installed OpticStudio text output.")]
    public async Task<SeidelCoefficientsData> ExecuteAsync(
        [Description("Wavelength number (0 for primary)")] int wavelength = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (wavelength < 0)
                throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be 0 (primary) or a positive wavelength number.");

            return await _session.ExecuteAsync("SeidelCoefficients",
                new Dictionary<string, object?> { ["wavelength"] = wavelength }, system =>
                {
                    var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                    if (wavelength > wavelengthCount)
                        throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be 0 or between 1 and {wavelengthCount}.");

                    var seidel = system.Analyses.New_SeidelCoefficients();
                    if (seidel == null)
                        throw new InvalidOperationException("OpticStudio did not create a Seidel Coefficients analysis.");
                    try
                    {
                        if (wavelength > 0)
                        {
                            if (seidel.GetSettings() is not ZOSAPI.Analysis.Settings.Aberrations.IAS_SeidelCoefficients settings)
                                throw new InvalidOperationException("OpticStudio did not expose Seidel settings through IAS_SeidelCoefficients.");
                            settings.Wavelength.SetWavelengthNumber(wavelength);
                        }

                        seidel.ApplyAndWaitForCompletion();
                        var results = seidel.GetResults() ?? throw new InvalidOperationException("Seidel Coefficients returned no results object.");
                        var tempFile = Path.Combine(Path.GetTempPath(), $"zemax_seidel_{Guid.NewGuid():N}.txt");
                        try
                        {
                            results.GetTextFile(tempFile);
                            if (!File.Exists(tempFile) || new FileInfo(tempFile).Length == 0)
                                throw new IOException("Seidel Coefficients produced no text results.");
                            return ParseSeidelTextFile(tempFile);
                        }
                        finally
                        {
                            try { File.Delete(tempFile); } catch { }
                        }
                    }
                    finally
                    {
                        seidel.Close();
                    }
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SeidelCoefficientsData { Success = false, Error = ex.Message };
        }
    }

    private static SeidelCoefficientsData ParseSeidelTextFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath);
        double wavelength = double.NaN, petzval = double.NaN, invariant = double.NaN;
        double chiefObj = double.NaN, chiefImg = double.NaN, margObj = double.NaN, margImg = double.NaN;
        var surfaceRows = new List<SeidelSurfaceRow>();
        SeidelSurfaceRow? totalRow = null;
        SeidelWavefrontSummary? wavefrontSummary = null;
        bool inSeidelCoeffs = false;
        bool inWavefrontSummary = false;
        bool pastSeidelHeader = false;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("Wavelength", StringComparison.OrdinalIgnoreCase) && trimmed.Contains(':')) { wavelength = ParseHeaderValue(trimmed); continue; }
            if (trimmed.StartsWith("Chief Ray Slope, Object", StringComparison.OrdinalIgnoreCase)) { chiefObj = ParseHeaderValue(trimmed); continue; }
            if (trimmed.StartsWith("Chief Ray Slope, Image", StringComparison.OrdinalIgnoreCase)) { chiefImg = ParseHeaderValue(trimmed); continue; }
            if (trimmed.StartsWith("Marginal Ray Slope, Object", StringComparison.OrdinalIgnoreCase)) { margObj = ParseHeaderValue(trimmed); continue; }
            if (trimmed.StartsWith("Marginal Ray Slope, Image", StringComparison.OrdinalIgnoreCase)) { margImg = ParseHeaderValue(trimmed); continue; }
            if (trimmed.StartsWith("Petzval radius", StringComparison.OrdinalIgnoreCase)) { petzval = ParseHeaderValue(trimmed); continue; }
            if (trimmed.StartsWith("Optical Invariant", StringComparison.OrdinalIgnoreCase)) { invariant = ParseHeaderValue(trimmed); continue; }

            if (trimmed.StartsWith("Seidel Aberration Coefficients:", StringComparison.OrdinalIgnoreCase) &&
                trimmed.IndexOf("Waves", StringComparison.OrdinalIgnoreCase) < 0)
            {
                inSeidelCoeffs = true;
                inWavefrontSummary = false;
                pastSeidelHeader = false;
                continue;
            }
            if (inSeidelCoeffs && trimmed.StartsWith("Seidel Aberration Coefficients in Waves", StringComparison.OrdinalIgnoreCase))
            {
                inSeidelCoeffs = false;
                continue;
            }
            if (trimmed.StartsWith("Wavefront Aberration Coefficient Summary", StringComparison.OrdinalIgnoreCase))
            {
                inSeidelCoeffs = false;
                inWavefrontSummary = true;
                continue;
            }

            if (inSeidelCoeffs)
            {
                if (trimmed.StartsWith("Surf", StringComparison.OrdinalIgnoreCase)) { pastSeidelHeader = true; continue; }
                if (!pastSeidelHeader || string.IsNullOrWhiteSpace(trimmed)) continue;
                var row = ParseSeidelRow(trimmed);
                if (row != null)
                {
                    if (row.Surface.Equals("TOT", StringComparison.OrdinalIgnoreCase)) totalRow = row;
                    else surfaceRows.Add(row);
                }
            }

            if (inWavefrontSummary && trimmed.StartsWith("TOT", StringComparison.OrdinalIgnoreCase))
            {
                var values = trimmed.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (wavefrontSummary == null && values.Length >= 8)
                {
                    wavefrontSummary = new SeidelWavefrontSummary
                    {
                        W040 = ParseDouble(values, 1), W131 = ParseDouble(values, 2), W222 = ParseDouble(values, 3),
                        W220P = ParseDouble(values, 4), W311 = ParseDouble(values, 5), W020 = ParseDouble(values, 6), W111 = ParseDouble(values, 7)
                    };
                }
                else if (wavefrontSummary != null && values.Length >= 4)
                {
                    wavefrontSummary = wavefrontSummary with
                    {
                        W220S = ParseDouble(values, 1), W220M = ParseDouble(values, 2), W220T = ParseDouble(values, 3)
                    };
                    inWavefrontSummary = false;
                }
            }
        }

        if (totalRow == null && surfaceRows.Count == 0)
            throw new InvalidDataException("Seidel text results contained no parsable coefficient table. The installed OpticStudio text format may be unsupported.");

        return new SeidelCoefficientsData
        {
            Success = true,
            Wavelength = wavelength,
            PetzvalRadius = petzval,
            OpticalInvariant = invariant,
            ChiefRaySlopeObject = chiefObj,
            ChiefRaySlopeImage = chiefImg,
            MarginalRaySlopeObject = margObj,
            MarginalRaySlopeImage = margImg,
            SurfaceCoefficients = surfaceRows.ToArray(),
            Total = totalRow,
            WavefrontSummary = wavefrontSummary
        };
    }

    private static double ParseHeaderValue(string line)
    {
        int colonIdx = line.LastIndexOf(':');
        if (colonIdx < 0) return double.NaN;
        var valueStr = line.Substring(colonIdx + 1).Trim().Split(' ')[0];
        return double.TryParse(valueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }

    private static SeidelSurfaceRow? ParseSeidelRow(string line)
    {
        var values = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (values.Length < 8) return null;
        return new SeidelSurfaceRow
        {
            Surface = values[0],
            S1_SPHA = ParseDouble(values, 1),
            S2_COMA = ParseDouble(values, 2),
            S3_ASTI = ParseDouble(values, 3),
            S4_FCUR = ParseDouble(values, 4),
            S5_DIST = ParseDouble(values, 5),
            CL_CLA = ParseDouble(values, 6),
            CT_CTR = ParseDouble(values, 7)
        };
    }

    private static double ParseDouble(string[] values, int index) =>
        index < values.Length && double.TryParse(values[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : double.NaN;
}
