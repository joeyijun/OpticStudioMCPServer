using System;
using System.Collections.Generic;
using System.Linq;

namespace ZemaxMCP.Toolsets;

/// <summary>
/// Authoritative, explicit MCP tool metadata shared by the Host and Worker.
/// Domain membership must never be inferred from a tool name: names are an API
/// surface and do not reliably describe the OpticStudio subsystem they affect.
/// </summary>
public static class ToolsetCatalog
{
    public const string BasicViewing = "basic-viewing";
    public const string SequentialDesign = "sequential-design";
    public const string NonSequentialStrayLight = "nonsequential-stray-light";
    public const string OptimizationTolerance = "optimization-tolerance";
    public const string FullExpert = "full-expert";

    public enum ToolImpact
    {
        ReadOnly,
        Caution,
        HighImpact
    }

    public sealed class Domain
    {
        public Domain(string id, string title, string purpose)
        {
            Id = id;
            Title = title;
            Purpose = purpose;
        }

        public string Id { get; }
        public string Title { get; }
        public string Purpose { get; }
    }

    public static readonly IReadOnlyList<Domain> Domains = new[]
    {
        new Domain("system", "System", "System state, catalog information, and safe inspection."),
        new Domain("sequential-editing", "Sequential editing", "Sequential lens data, fields, wavelengths, configurations, and system settings."),
        new Domain("non-sequential", "Non-sequential", "Non-sequential objects, detectors, and stray-light workflow."),
        new Domain("analysis", "Analysis", "Optical performance calculations and result inspection."),
        new Domain("optimization", "Optimization", "Merit functions, optimization, global search, and managed jobs."),
        new Domain("tolerance", "Tolerance", "Tolerance setup and tolerance-result inspection."),
        new Domain("polarization", "Polarization", "Polarization settings and related inspection."),
        new Domain("files", "Files", "Opening, saving, importing, and exporting project artifacts."),
        new Domain("administration", "Administration", "Connection, session, and service management.")
    };

    // Every registered MCP tool occurs once here.  Keep this table explicit so
    // changes are reviewed as semantic API changes rather than guessed from a
    // get_/set_ prefix or a word fragment in the tool name.
    private static readonly IReadOnlyDictionary<string, string> ToolDomains =
        new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["zemax_add_configuration_operand"] = "sequential-editing",
        ["zemax_add_operand"] = "optimization",
        ["zemax_add_surface"] = "sequential-editing",
        ["zemax_aperture_throughput"] = "analysis",
        ["zemax_cardinal_points"] = "analysis",
        ["zemax_chromatic_focal_shift"] = "analysis",
        ["zemax_clear_vignetting"] = "sequential-editing",
        ["zemax_connect"] = "administration",
        ["zemax_constrained_optimize"] = "optimization",
        ["zemax_delete_configuration_operand"] = "sequential-editing",
        ["zemax_diffraction_encircled_energy"] = "analysis",
        ["zemax_disconnect"] = "administration",
        ["zemax_export_analysis"] = "files",
        ["zemax_export_glass_catalog"] = "files",
        ["zemax_fft_mtf"] = "analysis",
        ["zemax_fft_mtf_vs_field"] = "analysis",
        ["zemax_fft_psf"] = "analysis",
        ["zemax_field_curvature_distortion"] = "analysis",
        ["zemax_filter_glasses"] = "system",
        ["zemax_forbes_merit_function"] = "optimization",
        ["zemax_geometric_encircled_energy"] = "analysis",
        ["zemax_geometric_image_analysis"] = "analysis",
        ["zemax_geometric_mtf"] = "analysis",
        ["zemax_geometric_mtf_vs_field"] = "analysis",
        ["zemax_get_advanced_system_settings"] = "system",
        ["zemax_get_afocal_mode"] = "system",
        ["zemax_get_aperture_settings"] = "system",
        ["zemax_get_apodization"] = "system",
        ["zemax_get_aspheric_surface"] = "sequential-editing",
        ["zemax_get_clear_semi_diameter_margin"] = "system",
        ["zemax_get_configuration"] = "sequential-editing",
        ["zemax_get_configuration_operands"] = "sequential-editing",
        ["zemax_get_environment"] = "system",
        ["zemax_get_extra_data"] = "sequential-editing",
        ["zemax_get_field_settings"] = "sequential-editing",
        ["zemax_get_first_order_data"] = "sequential-editing",
        ["zemax_get_glass_catalogs"] = "system",
        ["zemax_get_glasses"] = "system",
        ["zemax_get_global_matrix"] = "sequential-editing",
        ["zemax_get_material_catalog_settings"] = "system",
        ["zemax_get_merit_function"] = "optimization",
        ["zemax_get_mtf_units"] = "system",
        ["zemax_get_nonsequential_system_settings"] = "non-sequential",
        ["zemax_get_nsc_detector"] = "non-sequential",
        ["zemax_get_nsc_object_parameters"] = "non-sequential",
        ["zemax_get_nsc_objects"] = "non-sequential",
        ["zemax_get_polarization"] = "polarization",
        ["zemax_get_ray_aiming"] = "system",
        ["zemax_get_ray_aiming_settings"] = "system",
        ["zemax_get_stop_surface"] = "sequential-editing",
        ["zemax_get_surface"] = "sequential-editing",
        ["zemax_get_surface_aperture"] = "sequential-editing",
        ["zemax_get_surface_solves"] = "sequential-editing",
        ["zemax_get_system"] = "sequential-editing",
        ["zemax_get_system_files"] = "files",
        ["zemax_get_system_metadata"] = "system",
        ["zemax_get_tolerances"] = "tolerance",
        ["zemax_get_units"] = "system",
        ["zemax_get_variables"] = "optimization",
        ["zemax_get_vignetting"] = "sequential-editing",
        ["zemax_get_wavelength_settings"] = "sequential-editing",
        ["zemax_global_search"] = "optimization",
        ["zemax_hammer"] = "optimization",
        ["zemax_huygens_psf"] = "analysis",
        ["zemax_job_cancel"] = "optimization",
        ["zemax_job_list"] = "optimization",
        ["zemax_job_status"] = "optimization",
        ["zemax_lateral_color"] = "analysis",
        ["zemax_list_surface_types"] = "sequential-editing",
        ["zemax_load_merit_function_file"] = "files",
        ["zemax_longitudinal_aberration"] = "analysis",
        ["zemax_multistart_optimize"] = "optimization",
        ["zemax_multistart_status"] = "optimization",
        ["zemax_multistart_stop"] = "optimization",
        ["zemax_new_system"] = "files",
        ["zemax_opd_fan"] = "analysis",
        ["zemax_open_file"] = "files",
        ["zemax_operand_help"] = "optimization",
        ["zemax_optimization_wizard"] = "optimization",
        ["zemax_optimize"] = "optimization",
        ["zemax_pop"] = "analysis",
        ["zemax_pupil_aberration_fan"] = "analysis",
        ["zemax_quick_focus"] = "sequential-editing",
        ["zemax_ray_fan"] = "analysis",
        ["zemax_ray_trace"] = "analysis",
        ["zemax_ray_trace_extended"] = "analysis",
        ["zemax_relative_illumination"] = "analysis",
        ["zemax_remove_operand"] = "optimization",
        ["zemax_remove_surface"] = "sequential-editing",
        ["zemax_restart"] = "administration",
        ["zemax_rms_spot"] = "analysis",
        ["zemax_save_file"] = "files",
        ["zemax_save_merit_function_file"] = "files",
        ["zemax_scale_lens"] = "sequential-editing",
        ["zemax_search_operands"] = "optimization",
        ["zemax_seidel_coefficients"] = "analysis",
        ["zemax_set_afocal_mode"] = "system",
        ["zemax_set_aperture"] = "sequential-editing",
        ["zemax_set_apodization"] = "system",
        ["zemax_set_aspheric_surface"] = "sequential-editing",
        ["zemax_set_clear_semi_diameter_margin"] = "system",
        ["zemax_set_configuration_operand_value"] = "sequential-editing",
        ["zemax_set_current_configuration"] = "sequential-editing",
        ["zemax_set_environment"] = "system",
        ["zemax_set_extra_data"] = "sequential-editing",
        ["zemax_set_fields"] = "sequential-editing",
        ["zemax_set_mtf_units"] = "system",
        ["zemax_set_number_of_configurations"] = "sequential-editing",
        ["zemax_set_number_of_fields"] = "sequential-editing",
        ["zemax_set_number_of_wavelengths"] = "sequential-editing",
        ["zemax_set_off_axis_conic"] = "sequential-editing",
        ["zemax_set_polarization"] = "polarization",
        ["zemax_set_ray_aiming"] = "system",
        ["zemax_set_stop_surface"] = "sequential-editing",
        ["zemax_set_surface"] = "sequential-editing",
        ["zemax_set_surface_aperture"] = "sequential-editing",
        ["zemax_set_surface_parameter"] = "sequential-editing",
        ["zemax_set_surface_solve"] = "sequential-editing",
        ["zemax_set_surface_type"] = "sequential-editing",
        ["zemax_set_system_metadata"] = "system",
        ["zemax_set_variable_constraints"] = "optimization",
        ["zemax_set_vignetting"] = "sequential-editing",
        ["zemax_set_wavelengths"] = "sequential-editing",
        ["zemax_spot_diagram"] = "analysis",
        ["zemax_status"] = "administration",
        ["zemax_tool_catalog"] = "system"
    };

    // Tool impact is explicit metadata, not a get_/set_ naming heuristic. The
    // basic-viewing profile intentionally composes this with the domain table:
    // it can observe and analyse a sequential system, but cannot alter it.
    private static readonly HashSet<string> ReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
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
        "zemax_opd_fan", "zemax_operand_help", "zemax_pop", "zemax_pupil_aberration_fan", "zemax_ray_fan", "zemax_ray_trace", "zemax_ray_trace_extended",
        "zemax_relative_illumination", "zemax_rms_spot", "zemax_search_operands", "zemax_seidel_coefficients", "zemax_spot_diagram", "zemax_status", "zemax_tool_catalog"
    };

    private static readonly HashSet<string> CautionTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "zemax_connect", "zemax_disconnect", "zemax_job_cancel", "zemax_multistart_stop", "zemax_open_file", "zemax_restart"
    };

    private static readonly HashSet<string> HighImpactTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "zemax_add_configuration_operand", "zemax_add_operand", "zemax_add_surface", "zemax_clear_vignetting", "zemax_constrained_optimize",
        "zemax_delete_configuration_operand", "zemax_export_analysis", "zemax_export_glass_catalog", "zemax_forbes_merit_function", "zemax_global_search",
        "zemax_hammer", "zemax_load_merit_function_file", "zemax_multistart_optimize", "zemax_new_system", "zemax_optimization_wizard", "zemax_optimize",
        "zemax_quick_focus", "zemax_remove_operand", "zemax_remove_surface", "zemax_save_file", "zemax_save_merit_function_file", "zemax_scale_lens",
        "zemax_set_afocal_mode", "zemax_set_aperture", "zemax_set_apodization", "zemax_set_aspheric_surface", "zemax_set_clear_semi_diameter_margin",
        "zemax_set_configuration_operand_value", "zemax_set_current_configuration", "zemax_set_environment", "zemax_set_extra_data", "zemax_set_fields",
        "zemax_set_mtf_units", "zemax_set_number_of_configurations", "zemax_set_number_of_fields", "zemax_set_number_of_wavelengths", "zemax_set_off_axis_conic",
        "zemax_set_polarization", "zemax_set_ray_aiming", "zemax_set_stop_surface", "zemax_set_surface", "zemax_set_surface_aperture", "zemax_set_surface_parameter",
        "zemax_set_surface_solve", "zemax_set_surface_type", "zemax_set_system_metadata", "zemax_set_variable_constraints", "zemax_set_vignetting", "zemax_set_wavelengths"
    };

    public static IReadOnlyDictionary<string, string> ExplicitToolDomains => ToolDomains;

    public static string NormalizeProfile(string? profile)
    {
        var value = profile?.Trim().ToLowerInvariant();
        return value is BasicViewing or SequentialDesign or NonSequentialStrayLight or OptimizationTolerance or FullExpert
            ? value!
            : throw new ArgumentException("Toolset must be basic-viewing, sequential-design, nonsequential-stray-light, optimization-tolerance, or full-expert.");
    }

    public static IEnumerable<string> EnabledDomains(string profile) => NormalizeProfile(profile) switch
    {
        BasicViewing => new[] { "system", "sequential-editing", "analysis", "administration" },
        SequentialDesign => new[] { "system", "sequential-editing", "analysis", "polarization", "files", "administration" },
        NonSequentialStrayLight => new[] { "system", "non-sequential", "analysis", "files", "administration" },
        OptimizationTolerance => new[] { "system", "sequential-editing", "analysis", "optimization", "tolerance", "polarization", "files", "administration" },
        _ => Domains.Select(domain => domain.Id)
    };

    public static IEnumerable<string> EnabledImpacts(string profile) => NormalizeProfile(profile) == BasicViewing
        ? new[] { ToolImpact.ReadOnly.ToString() }
        : new[] { ToolImpact.ReadOnly.ToString(), ToolImpact.Caution.ToString(), ToolImpact.HighImpact.ToString() };

    public static ToolImpact GetImpact(string toolName)
    {
        if (!ToolDomains.ContainsKey(toolName)) throw new KeyNotFoundException("No explicit metadata is registered for MCP tool '" + toolName + "'.");
        if (ReadOnlyTools.Contains(toolName)) return ToolImpact.ReadOnly;
        if (CautionTools.Contains(toolName)) return ToolImpact.Caution;
        if (HighImpactTools.Contains(toolName)) return ToolImpact.HighImpact;
        throw new KeyNotFoundException("No explicit impact metadata is registered for MCP tool '" + toolName + "'.");
    }

    public static bool IsToolAllowed(string profile, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        return ToolDomains.TryGetValue(toolName!, out var domainId) &&
               EnabledDomains(profile).Contains(domainId, StringComparer.Ordinal) &&
               EnabledImpacts(profile).Contains(GetImpact(toolName!).ToString(), StringComparer.Ordinal);
    }

    public static string GetDomainId(string toolName)
    {
        if (ToolDomains.TryGetValue(toolName, out var domainId)) return domainId;
        throw new KeyNotFoundException("No explicit domain metadata is registered for MCP tool '" + toolName + "'.");
    }

    public static Domain GetDomain(string toolName) => Domains.First(domain => domain.Id == GetDomainId(toolName));
    public static int GetDomainOrder(string title) => Domains.Select((domain, index) => new { domain, index })
        .FirstOrDefault(item => item.domain.Title.Equals(title, StringComparison.Ordinal))?.index ?? int.MaxValue;
}
