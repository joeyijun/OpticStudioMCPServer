namespace ZemaxMCP.Core.Session;

/// <summary>
/// The single, explicit classification used by both the ZOS-API safety gate and
/// the MCP tool catalogue.  Do not infer impact from a method-name prefix: new
/// tools must be deliberately added here and unknown ZOS-API commands fail
/// closed as high impact.
/// </summary>
public enum ZemaxOperationImpact
{
    ReadOnly,
    Caution,
    HighImpact
}

public static class ZemaxOperationMetadata
{
    // Commands and MCP tools deliberately share the same policy records.  The
    // runtime maps below are projections of these records, not independent
    // safety lists that can silently drift apart.
    private static readonly OperationPolicy[] Policies = CreatePolicies();
    private static readonly Dictionary<string, ZemaxOperationImpact> Commands = BuildMap(Policies, policy => policy.Commands);
    private static readonly Dictionary<string, ZemaxOperationImpact> Tools = BuildMap(Policies, policy => policy.Tools);

    public static ZemaxOperationImpact GetCommandImpact(string commandName) =>
        Commands.TryGetValue(commandName, out var impact) ? impact : ZemaxOperationImpact.HighImpact;

    public static ZemaxOperationImpact GetToolImpact(string toolName) =>
        Tools.TryGetValue(toolName, out var impact) ? impact : ZemaxOperationImpact.Caution;

    public static bool IsKnownCommand(string commandName) => Commands.ContainsKey(commandName);
    public static bool IsKnownTool(string toolName) => Tools.ContainsKey(toolName);

    private static OperationPolicy[] CreatePolicies()
    {
        return new[]
        {
            new OperationPolicy(ZemaxOperationImpact.ReadOnly, new[]
            {
                "ApertureThroughput", "CardinalPoints", "ChromaticFocalShift", "DiffractionEncircledEnergy", "FftMtfVsField", "FftPsf",
                "FieldCurvatureDistortion", "GeometricEncircledEnergy", "GeometricImageAnalysis", "GeometricMTF", "GeometricMtfVsField",
                "GetAdvancedSystemSettings", "GetAfocalMode", "GetApertureSettings", "GetApodization", "GetAsphericSurface", "GetConfiguration",
                "GetConfigurationOperands", "GetExtraData", "GetFieldSettings", "GetFirstOrderData", "GetGlobalMatrix", "GetMaterialCatalogSettings",
                "GetMeritFunction", "GetMtfUnits", "GetNonSequentialSystemSettings", "GetNscDetector", "GetNscObjectParameters", "GetNscObjects",
                "GetRayAiming", "GetRayAimingSettings", "GetSurface", "GetSurfaceAperture", "GetSurfaceParameter", "GetSurfaceSolves", "GetSystem", "GetSystemFiles",
                "GetSystemMetadata", "GetTolerances", "GetUnits", "GetVariables", "GetWavelengthSettings", "HuygensPsf", "LateralColor",
                "LongitudinalAberration", "MTF", "OpdFan", "PupilAberrationFan", "RayFan", "RayTrace", "RayTraceExtended", "read",
                "RelativeIllumination", "RmsSpot", "SeidelCoefficients", "SpotDiagram"
            }, new[]
            {
            "zemax_aperture_throughput", "zemax_cardinal_points", "zemax_chromatic_focal_shift", "zemax_diffraction_encircled_energy", "zemax_fft_mtf", "zemax_fft_mtf_vs_field",
            "zemax_fft_psf", "zemax_field_curvature_distortion", "zemax_filter_glasses", "zemax_geometric_encircled_energy", "zemax_geometric_image_analysis",
            "zemax_geometric_mtf", "zemax_geometric_mtf_vs_field", "zemax_get_advanced_system_settings", "zemax_get_afocal_mode", "zemax_get_aperture_settings",
            "zemax_get_apodization", "zemax_get_aspheric_surface", "zemax_get_clear_semi_diameter_margin", "zemax_get_configuration",
            "zemax_get_configuration_operands", "zemax_get_environment", "zemax_get_extra_data", "zemax_get_field_settings", "zemax_get_first_order_data",
            "zemax_get_glass_catalogs", "zemax_get_glasses", "zemax_get_global_matrix", "zemax_get_material_catalog_settings", "zemax_get_merit_function",
            "zemax_get_mtf_units", "zemax_get_nonsequential_system_settings", "zemax_get_nsc_detector", "zemax_get_nsc_object_parameters", "zemax_get_nsc_objects",
            "zemax_get_polarization", "zemax_get_ray_aiming", "zemax_get_ray_aiming_settings", "zemax_get_stop_surface", "zemax_get_surface",
            "zemax_get_surface_aperture", "zemax_get_surface_solves", "zemax_get_system", "zemax_get_system_files", "zemax_get_system_metadata",
            "zemax_get_tolerances", "zemax_get_units", "zemax_get_variables", "zemax_get_vignetting", "zemax_get_wavelength_settings", "zemax_huygens_psf",
            "zemax_job_list", "zemax_job_status", "zemax_lateral_color", "zemax_list_surface_types", "zemax_longitudinal_aberration", "zemax_multistart_status",
            "zemax_opd_fan", "zemax_operand_help", "zemax_pupil_aberration_fan", "zemax_ray_fan", "zemax_ray_trace", "zemax_ray_trace_extended",
            "zemax_relative_illumination", "zemax_rms_spot", "zemax_search_operands", "zemax_seidel_coefficients", "zemax_spot_diagram", "zemax_status",
            "zemax_tool_catalog"
            }),
            new OperationPolicy(ZemaxOperationImpact.Caution, new[] { "OpenFile" }, new[]
            {
            "zemax_connect", "zemax_disconnect", "zemax_job_cancel", "zemax_multistart_stop", "zemax_open_file", "zemax_restart"
            }),
            new OperationPolicy(ZemaxOperationImpact.HighImpact, new[]
            {
                "AddConfigurationOperand", "AddOperand", "AddSurface", "calculate", "clear", "ConstrainedOptimize", "DeleteConfigurationOperand",
                "ExportAnalysis", "ForbesMeritFunction", "GlobalSearch", "Hammer", "LoadMeritFunctionFile", "MultistartOptimize", "NewSystem",
                "OptimizationWizard", "Optimize", "Pop", "QuickFocus", "RemoveOperand", "RemoveSurface", "SaveFile", "SaveMeritFunctionFile", "ScaleLens",
                "SetAfocalMode", "SetAperture", "SetApodization", "SetAsphericSurface", "SetConfigurationOperandValue", "SetCurrentConfiguration",
                "SetExtraData", "SetFields", "SetMtfUnits", "SetNumberOfConfigurations", "SetNumberOfFields", "SetNumberOfWavelengths", "SetOffAxisConic",
                "SetRayAiming", "SetSurface", "SetSurfaceAperture", "SetSurfaceParameter", "SetSurfaceSolve", "SetSurfaceType", "SetSystemMetadata",
                "SetVariableConstraints", "SetWavelengths"
            }, new[]
            {
            "zemax_add_configuration_operand", "zemax_add_operand", "zemax_add_surface", "zemax_clear_vignetting", "zemax_constrained_optimize",
            "zemax_delete_configuration_operand", "zemax_export_analysis", "zemax_export_glass_catalog", "zemax_forbes_merit_function", "zemax_global_search",
            "zemax_hammer", "zemax_load_merit_function_file", "zemax_multistart_optimize", "zemax_new_system", "zemax_optimization_wizard", "zemax_optimize",
            "zemax_pop", "zemax_quick_focus", "zemax_remove_operand", "zemax_remove_surface", "zemax_save_file", "zemax_save_merit_function_file", "zemax_scale_lens",
            "zemax_set_afocal_mode", "zemax_set_aperture", "zemax_set_apodization", "zemax_set_aspheric_surface", "zemax_set_clear_semi_diameter_margin",
            "zemax_set_configuration_operand_value", "zemax_set_current_configuration", "zemax_set_environment", "zemax_set_extra_data", "zemax_set_fields",
            "zemax_set_mtf_units", "zemax_set_number_of_configurations", "zemax_set_number_of_fields", "zemax_set_number_of_wavelengths", "zemax_set_polarization",
            "zemax_set_ray_aiming", "zemax_set_stop_surface", "zemax_set_surface", "zemax_set_surface_aperture", "zemax_set_surface_parameter", "zemax_set_surface_solve",
            "zemax_set_surface_type", "zemax_set_system_metadata", "zemax_set_variable_constraints", "zemax_set_vignetting", "zemax_set_wavelengths", "zemax_set_off_axis_conic"
            })
        };
    }

    private static Dictionary<string, ZemaxOperationImpact> BuildMap(OperationPolicy[] policies, Func<OperationPolicy, IEnumerable<string>> selectNames)
    {
        var map = new Dictionary<string, ZemaxOperationImpact>(StringComparer.OrdinalIgnoreCase);
        foreach (var policy in policies)
            foreach (var name in selectNames(policy))
                map.Add(name, policy.Impact);
        return map;
    }

    private sealed class OperationPolicy
    {
        public OperationPolicy(ZemaxOperationImpact impact, IEnumerable<string> commands, IEnumerable<string> tools)
        {
            Impact = impact;
            Commands = commands;
            Tools = tools;
        }

        public ZemaxOperationImpact Impact { get; }
        public IEnumerable<string> Commands { get; }
        public IEnumerable<string> Tools { get; }
    }
}
