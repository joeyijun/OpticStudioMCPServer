using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[ZemaxToolType]
public sealed class ApertureSettingsTool
{
    private readonly IZemaxSession _session;
    public ApertureSettingsTool(IZemaxSession session) => _session = session;

    public record ApertureSettingsResult(bool Success, string? Error, string ApertureType, double ApertureValue,
        string ApodizationType, double ApodizationFactor, bool ApodizationFactorIsUsed, bool AfocalImageSpace,
        bool TelecentricObjectSpace, bool FastSemiDiameters, bool CheckGrinApertures,
        bool IterateSolvesWhenUpdating, double SemiDiameterMargin, double SemiDiameterMarginPercent);

    [ZemaxTool(Name = "zemax_get_aperture_settings")]
    [Description("Read complete System Explorer aperture settings, including apodization, afocal/telecentric modes, semi-diameter calculation options, and margins.")]
    public async Task<ApertureSettingsResult> ExecuteAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetApertureSettings", null, system =>
            {
                var aperture = system.SystemData.Aperture;
                return new ApertureSettingsResult(true, null, aperture.ApertureType.ToString(), aperture.ApertureValue.Sanitize(),
                    aperture.ApodizationType.ToString(), aperture.ApodizationFactor.Sanitize(), aperture.ApodizationFactorIsUsed,
                    aperture.AFocalImageSpace, aperture.TelecentricObjectSpace, aperture.FastSemiDiameters,
                    aperture.CheckGRINApertures, aperture.IterateSolvesWhenUpdating, aperture.SemiDiameterMargin.Sanitize(),
                    aperture.SemiDiameterMarginPct.Sanitize());
            });
        }
        catch (Exception ex)
        {
            return new ApertureSettingsResult(false, ex.Message, "", 0, "", 0, false, false, false, false, false, false, 0, 0);
        }
    }
}
