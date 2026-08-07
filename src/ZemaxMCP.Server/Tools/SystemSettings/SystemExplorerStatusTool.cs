using System.ComponentModel;
using System.Globalization;
using System.Reflection;
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
        bool UseRayAimingCache, int? CacheSetupSteps, bool? UseAdvancedConvergence,
        bool? UseEnhancedRayAiming, bool UseRobustRayAiming, bool? UseFallbackSearchDuringCacheSetup,
        IReadOnlyList<string> UnsupportedSettings);

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
    [Description("Read ray-aiming and pupil-shift settings. Options added after early OpticStudio 2021 releases are capability-detected at runtime; unsupported fields return null and are listed in UnsupportedSettings rather than making the entire Worker incompatible.")]
    public async Task<RayAimingSettingsResult> GetRayAimingAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetRayAimingSettings", null, system =>
            {
                var data = system.SystemData.RayAiming;
                var unsupported = new List<string>();

                // Enhanced Ray Aiming was experimental during 2021 and became a
                // formal feature in 22.1; Advanced Convergence/Fallback/Steps
                // were added during that transition. Keep these names out of
                // compile-time interface calls so a Worker built against a 2021
                // baseline can still expose the older ray-aiming settings.
                var cacheSetupSteps = ReadOptionalValue<int>(data, "NumStepsCacheSetup", unsupported);
                var useAdvancedConvergence = ReadOptionalValue<bool>(data, "UseAdvancedConvergence", unsupported);
                var useEnhancedRayAiming = ReadOptionalValue<bool>(data, "UseEnhancedRayAiming", unsupported);
                var useFallbackSearch = ReadOptionalValue<bool>(data, "UseFallBackSearchDuringCacheSetup", unsupported);

                return new RayAimingSettingsResult(true, null, data.RayAiming.ToString(), data.Method.ToString(),
                    data.AutomaticallyCalculatePupilShiftsIsChecked, data.PupilShiftX.Sanitize(), data.PupilShiftY.Sanitize(),
                    data.PupilShiftZ.Sanitize(), data.PupilCompressX.Sanitize(), data.PupilCompressY.Sanitize(),
                    data.ScalePupilShiftFactorsByField, data.UseRayAimingCache, cacheSetupSteps,
                    useAdvancedConvergence, useEnhancedRayAiming, data.UseRobustRayAiming,
                    useFallbackSearch, unsupported);
            });
        }
        catch (Exception ex)
        {
            return new RayAimingSettingsResult(false, ex.Message, "", "", false, 0, 0, 0, 0, 0,
                false, false, null, null, null, false, null, Array.Empty<string>());
        }
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

    private static T? ReadOptionalValue<T>(object target, string propertyName, ICollection<string> unsupported)
        where T : struct
    {
        var property = FindProperty(target, propertyName);
        if (property == null)
        {
            unsupported.Add(propertyName);
            return null;
        }

        try
        {
            var value = property.GetValue(target, null);
            if (value is T typed) return typed;
            if (value == null)
                throw new InvalidDataException($"Optional ZOS-API property {propertyName} returned null unexpectedly.");
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException($"Reading optional ZOS-API property {propertyName} failed: {ex.InnerException.Message}", ex.InnerException);
        }
    }

    private static PropertyInfo? FindProperty(object target, string propertyName)
    {
        var runtimeType = target.GetType();
        var property = runtimeType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property != null) return property;
        foreach (var interfaceType in runtimeType.GetInterfaces())
        {
            property = interfaceType.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property != null) return property;
        }
        return null;
    }
}
