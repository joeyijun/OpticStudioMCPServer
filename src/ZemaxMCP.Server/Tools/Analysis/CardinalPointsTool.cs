using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class CardinalPointsTool
{
    private readonly IZemaxSession _session;

    public CardinalPointsTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_cardinal_points")]
    [Description("Get first-order focal length, pupil and paraxial magnification data without modifying the user's Merit Function Editor.")]
    public async Task<CardinalPoints> ExecuteAsync(
        [Description("Wavelength number (1-indexed)")] int wavelength = 1,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (wavelength < 1)
                throw new ArgumentOutOfRangeException(nameof(wavelength), "Wavelength number must be at least 1.");

            return await _session.ExecuteAsync("CardinalPoints",
                new Dictionary<string, object?> { ["wavelength"] = wavelength },
                system =>
                {
                    var wavelengthCount = system.SystemData.Wavelengths.NumberOfWavelengths;
                    if (wavelength > wavelengthCount)
                        throw new ArgumentOutOfRangeException(nameof(wavelength), $"Wavelength must be between 1 and {wavelengthCount}.");

                    var mfe = system.MFE;
                    var effl = mfe.GetOperandValue(ZOSAPI.Editors.MFE.MeritOperandType.EFFL,
                        0, wavelength, 0, 0, 0, 0, 0, 0);
                    var enpp = mfe.GetOperandValue(ZOSAPI.Editors.MFE.MeritOperandType.ENPP,
                        0, 0, 0, 0, 0, 0, 0, 0);
                    var epdi = mfe.GetOperandValue(ZOSAPI.Editors.MFE.MeritOperandType.EPDI,
                        0, 0, 0, 0, 0, 0, 0, 0);
                    var expp = mfe.GetOperandValue(ZOSAPI.Editors.MFE.MeritOperandType.EXPP,
                        0, 0, 0, 0, 0, 0, 0, 0);
                    var expd = mfe.GetOperandValue(ZOSAPI.Editors.MFE.MeritOperandType.EXPD,
                        0, 0, 0, 0, 0, 0, 0, 0);
                    var pmag = mfe.GetOperandValue(ZOSAPI.Editors.MFE.MeritOperandType.PMAG,
                        0, wavelength, 0, 0, 0, 0, 0, 0).Sanitize();

                    var lde = system.LDE;
                    if (lde.NumberOfSurfaces < 2)
                        throw new InvalidOperationException("Cardinal-point readback requires a sequential system with an image surface.");
                    var lastLensSurface = Math.Max(0, lde.NumberOfSurfaces - 2);
                    var bfl = lde.GetSurfaceAt(lastLensSurface).Thickness;
                    var ffl = enpp - effl;
                    var objectDistance = lde.GetSurfaceAt(0).Thickness;

                    return new CardinalPoints
                    {
                        Success = true,
                        EffectiveFocalLength = effl.OpticalDimension(),
                        BackFocalLength = bfl.OpticalDimension(),
                        FrontFocalLength = ffl.OpticalDimension(),
                        EntrancePupilPosition = enpp.OpticalDimension(),
                        EntrancePupilDiameter = epdi.OpticalDimension(),
                        ExitPupilPosition = expp.OpticalDimension(),
                        ExitPupilDiameter = expd.OpticalDimension(),
                        ImageDistance = bfl.OpticalDimension(),
                        ObjectDistance = objectDistance.OpticalDimension(),
                        DimensionStates = new Dictionary<string, string>
                        {
                            ["effectiveFocalLength"] = effl.OpticalDimensionState(),
                            ["backFocalLength"] = bfl.OpticalDimensionState(),
                            ["frontFocalLength"] = ffl.OpticalDimensionState(),
                            ["entrancePupilPosition"] = enpp.OpticalDimensionState(),
                            ["entrancePupilDiameter"] = epdi.OpticalDimensionState(),
                            ["exitPupilPosition"] = expp.OpticalDimensionState(),
                            ["exitPupilDiameter"] = expd.OpticalDimensionState(),
                            ["imageDistance"] = bfl.OpticalDimensionState(),
                            ["objectDistance"] = objectDistance.OpticalDimensionState()
                        },
                        Magnification = pmag,
                        Wavelength = wavelength
                    };
                }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CardinalPoints
            {
                Success = false,
                Error = ex.Message,
                Wavelength = wavelength
            };
        }
    }
}
