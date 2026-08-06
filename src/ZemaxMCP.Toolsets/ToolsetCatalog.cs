using System;
using System.Collections.Generic;
using System.Linq;

namespace ZemaxMCP.Toolsets;

/// <summary>
/// The authoritative mapping from MCP tool name to a user-facing domain and
/// Launcher run configuration. Both the HTTP Host and the Worker catalog link
/// this source file so their view of the available surface cannot drift.
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
        if (toolName!.Equals("zemax_tool_catalog", StringComparison.Ordinal)) return true;
        return EnabledDomains(profile).Contains(GetDomainId(toolName), StringComparer.Ordinal);
    }

    public static string GetDomainId(string toolName)
    {
        if (toolName.IndexOf("polarization", StringComparison.OrdinalIgnoreCase) >= 0) return "polarization";
        if (toolName.IndexOf("tolerance", StringComparison.OrdinalIgnoreCase) >= 0) return "tolerance";
        if (toolName.IndexOf("nsc_", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("nonsequential", StringComparison.OrdinalIgnoreCase) >= 0) return "non-sequential";
        if (toolName.IndexOf("merit", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("optimiz", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("operand", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("global_search", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("hammer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("forbes", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("variables", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("multistart", StringComparison.OrdinalIgnoreCase) >= 0 ||
            toolName.IndexOf("job_", StringComparison.OrdinalIgnoreCase) >= 0) return "optimization";
        if (toolName is "zemax_open_file" or "zemax_save_file" or "zemax_new_system" or "zemax_export_analysis" or "zemax_export_glass_catalog") return "files";
        if (toolName.StartsWith("zemax_get_", StringComparison.Ordinal) ||
            toolName.StartsWith("zemax_set_", StringComparison.Ordinal) ||
            toolName.StartsWith("zemax_add_surface", StringComparison.Ordinal) ||
            toolName.StartsWith("zemax_remove_surface", StringComparison.Ordinal) ||
            toolName is "zemax_quick_focus" or "zemax_scale_lens" or "zemax_clear_vignetting" or "zemax_list_surface_types") return "sequential-editing";
        if (toolName is "zemax_connect" or "zemax_disconnect" or "zemax_restart" or "zemax_status") return "administration";
        return IsAnalysisTool(toolName) ? "analysis" : "system";
    }

    public static Domain GetDomain(string toolName) => Domains.First(domain => domain.Id == GetDomainId(toolName));
    public static int GetDomainOrder(string title) => Domains.Select((domain, index) => new { domain, index })
        .FirstOrDefault(item => item.domain.Title.Equals(title, StringComparison.Ordinal))?.index ?? int.MaxValue;

    private static bool IsAnalysisTool(string name) => name is
        "zemax_spot_diagram" or "zemax_rms_spot" or "zemax_ray_trace" or "zemax_ray_trace_extended" or
        "zemax_fft_mtf" or "zemax_fft_mtf_vs_field" or "zemax_geometric_mtf" or "zemax_geometric_mtf_vs_field" or
        "zemax_fft_psf" or "zemax_huygens_psf" or "zemax_pop" or "zemax_cardinal_points" or
        "zemax_seidel_coefficients" or "zemax_lateral_color" or "zemax_longitudinal_aberration" or
        "zemax_chromatic_focal_shift" or "zemax_field_curvature_distortion" or "zemax_ray_fan" or
        "zemax_opd_fan" or "zemax_pupil_aberration_fan" or "zemax_diffraction_encircled_energy" or
        "zemax_geometric_encircled_energy" or "zemax_relative_illumination" or "zemax_geometric_image_analysis";
}
