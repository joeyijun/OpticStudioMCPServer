using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class SpotDiagramTool
{
    private readonly IZemaxSession _session;

    public SpotDiagramTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_spot_diagram")]
    [Description("Evaluate centroid-referenced RMS spot radius without modifying the user's Merit Function Editor. Returns Gaussian-quadrature RSCE and rectangular-grid RSRE values.")]
    public async Task<SpotDiagramData> ExecuteAsync(
        [Description("Field number (1-indexed)")] int field = 1,
        [Description("Wavelength number (0 for wavelength-weighted polychromatic)")] int wavelength = 0,
        [Description("Sampling parameter: Gaussian rings for RSCE and grid sampling value for RSRE (1-6)")] int rings = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (field < 1) throw new ArgumentOutOfRangeException(nameof(field), "Field number must be at least 1.");
            if (wavelength < 0) throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength must be 0 (polychromatic) or a positive wavelength number.");
            if (rings < 1 || rings > 6) throw new ArgumentOutOfRangeException(nameof(rings), "Sampling value must be between 1 and 6.");

            var parameters = new Dictionary<string, object?>
            {
                ["field"] = field,
                ["wavelength"] = wavelength,
                ["rings"] = rings
            };

            return await _session.ExecuteAsync("SpotDiagram", parameters, system =>
            {
                var fields = system.SystemData.Fields;
                if (field > fields.NumberOfFields)
                    throw new ArgumentOutOfRangeException(nameof(field), $"Field must be between 1 and {fields.NumberOfFields}.");
                var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                if (wavelength > wavelengthCount)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be 0 or between 1 and {wavelengthCount}.");

                var fieldObj = fields.GetField(field);
                var (hx, hy) = GetNormalizedField(fields, fieldObj.X, fieldObj.Y);
                var mfe = system.MFE;

                // IMeritFunctionEditor.GetOperandValue evaluates an operand even
                // when it is not present in the MFE, so this ReadOnly tool never
                // adds/removes rows from the user's optimization merit function.
                var rmsSpot = mfe.GetOperandValue(
                    ZOSAPI.Editors.MFE.MeritOperandType.RSCE,
                    rings, wavelength, hx, hy, 0, 0, 0, 0);
                var rectangularSpot = mfe.GetOperandValue(
                    ZOSAPI.Editors.MFE.MeritOperandType.RSRE,
                    rings, wavelength, hx, hy, 0, 0, 0, 0);

                return new SpotDiagramData
                {
                    Success = true,
                    RmsSpotSizeX = rmsSpot,
                    RmsSpotSizeY = rmsSpot,
                    RmsSpotRadius = rmsSpot,
                    GeoSpotSizeX = rectangularSpot,
                    GeoSpotSizeY = rectangularSpot,
                    GeoSpotRadius = rectangularSpot,
                    CentroidX = 0,
                    CentroidY = 0,
                    AiryRadius = 0,
                    Field = field,
                    Wavelength = wavelength,
                    DataDescription = $"RSCE Gaussian RMS spot radius: {rmsSpot:F4}; RSRE rectangular-grid RMS spot radius: {rectangularSpot:F4}"
                };
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SpotDiagramData
            {
                Success = false,
                Error = ex.Message,
                Field = field,
                Wavelength = wavelength
            };
        }
    }

    private static (double Hx, double Hy) GetNormalizedField(dynamic fields, double x, double y)
    {
        var normalization = fields.Normalization.ToString();
        if (string.Equals(normalization, "Rectangular", StringComparison.OrdinalIgnoreCase))
        {
            double maxX = 0, maxY = 0;
            for (var i = 1; i <= fields.NumberOfFields; i++)
            {
                var current = fields.GetField(i);
                maxX = Math.Max(maxX, Math.Abs((double)current.X));
                maxY = Math.Max(maxY, Math.Abs((double)current.Y));
            }
            return (maxX > 0 ? x / maxX : 0.0, maxY > 0 ? y / maxY : 0.0);
        }

        double maxRadius = 0;
        for (var i = 1; i <= fields.NumberOfFields; i++)
        {
            var current = fields.GetField(i);
            var currentX = (double)current.X;
            var currentY = (double)current.Y;
            maxRadius = Math.Max(maxRadius, Math.Sqrt(currentX * currentX + currentY * currentY));
        }
        return maxRadius > 0 ? (x / maxRadius, y / maxRadius) : (0.0, 0.0);
    }
}
