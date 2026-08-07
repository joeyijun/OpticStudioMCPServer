using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SetApertureTool
{
    private readonly IZemaxSession _session;

    public SetApertureTool(IZemaxSession session) => _session = session;

    public record SetApertureResult(
        bool Success,
        string? Error,
        string ApertureType,
        double ApertureValue
    );

    [ZemaxTool(Name = "zemax_set_aperture")]
    [Description("Set the system aperture")]
    public async Task<SetApertureResult> ExecuteAsync(
        [Description("Aperture value (diameter, F/#, NA, etc.)")] double value,
        [Description("Aperture type: EPD, FNumber, ObjectNA, FloatByStop")] string apertureType = "EPD",
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Aperture value must be finite and positive.");
            if (string.IsNullOrWhiteSpace(apertureType))
                throw new ArgumentException("Aperture type is required.", nameof(apertureType));
            var apType = apertureType.Trim().ToUpperInvariant() switch
            {
                "EPD" or "ENTRANCEPUPILDIAMETER" => ZOSAPI.SystemData.ZemaxApertureType.EntrancePupilDiameter,
                "FNUMBER" or "IMAGESPACEFNUM" => ZOSAPI.SystemData.ZemaxApertureType.ImageSpaceFNum,
                "OBJECTNA" or "OBJECTSPACENA" => ZOSAPI.SystemData.ZemaxApertureType.ObjectSpaceNA,
                "FLOATBYSTOP" or "FLOATBYSTOPSIZE" => ZOSAPI.SystemData.ZemaxApertureType.FloatByStopSize,
                _ => throw new ArgumentException("Aperture type must be EPD, FNumber, ObjectNA, or FloatByStop.", nameof(apertureType))
            };

            var parameters = new Dictionary<string, object?>
            {
                ["value"] = value,
                ["apertureType"] = apertureType
            };

            var result = await _session.ExecuteAsync("SetAperture", parameters, system =>
            {
                var aperture = system.SystemData.Aperture;
                aperture.ApertureType = apType;
                aperture.ApertureValue = value;

                return new SetApertureResult(
                    Success: true,
                    Error: null,
                    ApertureType: aperture.ApertureType.ToString(),
                    ApertureValue: aperture.ApertureValue.Sanitize()
                );
            }, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            return new SetApertureResult(false, ex.Message, apertureType, value);
        }
    }
}
