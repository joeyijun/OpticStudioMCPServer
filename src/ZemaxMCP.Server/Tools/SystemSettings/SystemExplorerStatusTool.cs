using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[ZemaxToolType]
public sealed class SystemExplorerStatusTool
{
    private readonly IZemaxSession _session;
    public SystemExplorerStatusTool(IZemaxSession session) => _session = session;

    public record AdvancedSettingsResult(bool Success, string? Error, string ReferenceOpd, string ParaxialRays,
        string FNumberMethod, string HuygensIntegralMethod, bool OpdModulo2Pi, bool TurnOffThreading,
        bool IncludeCalculatedDataInSessionFile, bool IncludeToleranceDataInSessionFile,
        bool DontPrintCoordinateBreakData);

    public record RayAimingSettingsResult(bool Success, string? Error, string RayAiming, string Method,
        bool AutomaticallyCalculatePupilShifts, double PupilShiftX, double PupilShiftY, double PupilShiftZ,
        double PupilCompressX, double PupilCompressY, bool ScalePupilShiftFactorsByField,
        bool UseRayAimingCache, int CacheSetupSteps, bool UseAdvancedConvergence,
        bool UseEnhancedRayAiming, bool UseRobustRayAiming, bool UseFallbackSearchDuringCacheSetup);

    public record MaterialCatalogSettingsResult(bool Success, string? Error, IReadOnlyList<string> CatalogsInUse,
        IReadOnlyList<string> AvailableCatalogs);

    public record NonSequentialSettingsResult(bool Success, string? Error, string SystemMode,
        int MaximumIntersectionsPerRay, int MaximumSegmentsPerRay, int MaximumNestedTouchingObjects,
        int MaximumSourceFileRaysInMemory, double MinimumRelativeRayIntensity, double MinimumAbsoluteRayIntensity,
        double GlueDistanceInLensUnits, double MissedRayDrawDistanceInLensUnits, bool SimpleRaySplitting,
        bool RetraceSourceRaysUponFileOpen);

    [ZemaxTool(Name = "zemax_get_advanced_system_settings")]
    [Description("Read System Explorer advanced settings for OPD reference, paraxial rays, F-number computation, Huygens integration, threading, and session files.")]
    public async Task<AdvancedSettingsResult> GetAdvancedAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetAdvancedSystemSettings", null, system =>
            {
                var data = system.SystemData.Advanced;
                return new AdvancedSettingsResult(true, null, data.ReferenceOPD.ToString(), data.ParaxialRays.ToString(),
                    data.FNumMethod.ToString(), data.HuygensIntegralMethod.ToString(), data.OPDModulo2PI,
                    data.TurnOffThreading, data.IncludeCalculatedDataInSessionFile,
                    data.IncludeToleranceDataInSessionFile, data.DontPrintCoordinateBreakData);
            });
        }
        catch (Exception ex) { return new AdvancedSettingsResult(false, ex.Message, "", "", "", "", false, false, false, false, false); }
    }

    [ZemaxTool(Name = "zemax_get_ray_aiming_settings")]
    [Description("Read complete ray-aiming and pupil-shift settings, including cache and convergence options.")]
    public async Task<RayAimingSettingsResult> GetRayAimingAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetRayAimingSettings", null, system =>
            {
                var data = system.SystemData.RayAiming;
                return new RayAimingSettingsResult(true, null, data.RayAiming.ToString(), data.Method.ToString(),
                    data.AutomaticallyCalculatePupilShiftsIsChecked, data.PupilShiftX.Sanitize(), data.PupilShiftY.Sanitize(),
                    data.PupilShiftZ.Sanitize(), data.PupilCompressX.Sanitize(), data.PupilCompressY.Sanitize(),
                    data.ScalePupilShiftFactorsByField, data.UseRayAimingCache, data.NumStepsCacheSetup,
                    data.UseAdvancedConvergence, data.UseEnhancedRayAiming, data.UseRobustRayAiming,
                    data.UseFallBackSearchDuringCacheSetup);
            });
        }
        catch (Exception ex) { return new RayAimingSettingsResult(false, ex.Message, "", "", false, 0, 0, 0, 0, 0, false, false, 0, false, false, false, false); }
    }

    [ZemaxTool(Name = "zemax_get_material_catalog_settings")]
    [Description("Read material catalogs currently used by the optical system; optionally include all catalogs available to OpticStudio.")]
    public async Task<MaterialCatalogSettingsResult> GetMaterialCatalogsAsync(
        [Description("Include all available material catalogs; disabled by default to keep the response compact")] bool includeAvailable = false)
    {
        try
        {
            return await _session.ExecuteAsync("GetMaterialCatalogSettings", new Dictionary<string, object?>
            {
                ["includeAvailable"] = includeAvailable
            }, system =>
            {
                var data = system.SystemData.MaterialCatalogs;
                return new MaterialCatalogSettingsResult(true, null, data.GetCatalogsInUse() ?? Array.Empty<string>(),
                    includeAvailable ? data.GetAvailableCatalogs() ?? Array.Empty<string>() : Array.Empty<string>());
            });
        }
        catch (Exception ex) { return new MaterialCatalogSettingsResult(false, ex.Message, Array.Empty<string>(), Array.Empty<string>()); }
    }

    [ZemaxTool(Name = "zemax_get_nonsequential_system_settings")]
    [Description("Read non-sequential ray limits, intensity thresholds, glue/missed-ray distances, splitting, and source-file retrace settings. Intended for NSC systems.")]
    public async Task<NonSequentialSettingsResult> GetNonSequentialAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetNonSequentialSystemSettings", null, system =>
            {
                var data = system.SystemData.NonSequentialData;
                return new NonSequentialSettingsResult(true, null, system.Mode.ToString(), data.MaximumIntersectionsPerRay,
                    data.MaximumSegmentsPerRay, data.MaximumNestedTouchingObjects, data.MaximumSourceFileRaysInMemory,
                    data.MinimumRelativeRayIntensity.Sanitize(), data.MinimumAbsoluteRayIntensity.Sanitize(),
                    data.GlueDistanceInLensUnits.Sanitize(), data.MissedRayDrawDistanceInLensUnits.Sanitize(),
                    data.SimpleRaySplitting, data.RetraceSourceRaysUponFileOpen);
            });
        }
        catch (Exception ex) { return new NonSequentialSettingsResult(false, ex.Message, "", 0, 0, 0, 0, 0, 0, 0, 0, false, false); }
    }
}
