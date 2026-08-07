using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class RmsSpotTool
{
    private readonly IZemaxSession _session;

    public RmsSpotTool(IZemaxSession session) => _session = session;

    public record RmsSpotResult(
        bool Success,
        string? Error,
        double RmsSpotRadius,
        double Hx,
        double Hy,
        int Wavelength,
        string Reference,
        string Method
    );

    [ZemaxTool(Name = "zemax_rms_spot")]
    [Description("Calculate RMS spot radius for a normalized field point without modifying the user's Merit Function Editor.")]
    public async Task<RmsSpotResult> ExecuteAsync(
        [Description("Normalized field x coordinate (-1 to 1)")] double hx = 0,
        [Description("Normalized field y coordinate (-1 to 1)")] double hy = 0,
        [Description("Wavelength number (0 for wavelength-weighted polychromatic)")] int wavelength = 0,
        [Description("Reference: centroid or chief")] string reference = "centroid",
        [Description("Gaussian ring count or rectangular-grid sample count; must be positive")] int sampling = 4,
        [Description("Use rectangular grid (true) or Gaussian quadrature (false)")] bool useGrid = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateNormalized(hx, nameof(hx));
            ValidateNormalized(hy, nameof(hy));
            if (wavelength < 0)
                throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be 0 (polychromatic) or a positive wavelength number.");
            if (sampling < 1)
                throw new ArgumentOutOfRangeException(nameof(sampling), "Sampling must be a positive integer.");
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException("Reference is required.", nameof(reference));

            var normalizedReference = reference.Trim().ToLowerInvariant() switch
            {
                "centroid" => "centroid",
                "chief" => "chief",
                _ => throw new ArgumentException("Reference must be centroid or chief.", nameof(reference))
            };

            var operandType = (normalizedReference, useGrid) switch
            {
                ("centroid", false) => ZOSAPI.Editors.MFE.MeritOperandType.RSCE,
                ("centroid", true) => ZOSAPI.Editors.MFE.MeritOperandType.RSRE,
                ("chief", false) => ZOSAPI.Editors.MFE.MeritOperandType.RSCH,
                ("chief", true) => ZOSAPI.Editors.MFE.MeritOperandType.RSRH,
                _ => throw new InvalidOperationException("Unsupported RMS spot operand selection.")
            };

            var parameters = new Dictionary<string, object?>
            {
                ["hx"] = hx,
                ["hy"] = hy,
                ["wavelength"] = wavelength,
                ["reference"] = normalizedReference,
                ["sampling"] = sampling,
                ["useGrid"] = useGrid
            };

            return await _session.ExecuteAsync("RmsSpot", parameters, system =>
            {
                var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                if (wavelength > wavelengthCount)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be 0 or between 1 and {wavelengthCount}.");

                // GetOperandValue evaluates an operand without adding it to the
                // MFE, keeping this ReadOnly tool side-effect free even on errors.
                var rmsValue = system.MFE.GetOperandValue(
                    operandType,
                    sampling, wavelength, hx, hy, 0, 0, 0, 0);

                return new RmsSpotResult(
                    Success: true,
                    Error: null,
                    RmsSpotRadius: rmsValue,
                    Hx: hx,
                    Hy: hy,
                    Wavelength: wavelength,
                    Reference: normalizedReference,
                    Method: useGrid ? "Rectangular Grid" : "Gaussian Quadrature"
                );
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new RmsSpotResult(false, ex.Message, 0, hx, hy, wavelength, reference, "");
        }
    }

    private static void ValidateNormalized(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < -1 || value > 1)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and between -1 and 1.");
    }
}
