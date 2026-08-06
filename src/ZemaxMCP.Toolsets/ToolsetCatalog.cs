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
        BasicViewing => new[] { "system", "analysis", "administration" },
        SequentialDesign => new[] { "system", "sequential-editing", "analysis", "polarization", "files", "administration" },
        NonSequentialStrayLight => new[] { "system", "non-sequential", "analysis", "files", "administration" },
        OptimizationTolerance => new[] { "system", "sequential-editing", "analysis", "optimization", "tolerance", "polarization", "files", "administration" },
        _ => Domains.Select(domain => domain.Id)
    };

    public static bool IsToolAllowed(string profile, string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return false;
        return ToolDomains.TryGetValue(toolName!, out var domainId) &&
               EnabledDomains(profile).Contains(domainId, StringComparer.Ordinal);
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
