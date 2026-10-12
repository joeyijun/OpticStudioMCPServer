using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZemaxMCP.ToolManifest;

/// <summary>Explicit, version-matched background capabilities shared by Host and Worker.</summary>
public static class BackgroundToolPolicy
{
    private static readonly HashSet<string> NativeJobs = new(StringComparer.Ordinal)
    {
        "zemax_pop", "zemax_run_nsc_ray_trace", "zemax_run_tolerancing",
        "zemax_global_search", "zemax_multistart_optimize"
    };
    private static readonly HashSet<string> SharedRunner = new(StringComparer.Ordinal)
    {
        "zemax_optimize", "zemax_constrained_optimize", "zemax_hammer",
        "zemax_fft_mtf", "zemax_fft_mtf_vs_field", "zemax_geometric_mtf", "zemax_geometric_mtf_vs_field",
        "zemax_fft_psf", "zemax_huygens_psf", "zemax_spot_diagram", "zemax_rms_spot",
        "zemax_diffraction_encircled_energy", "zemax_geometric_encircled_energy", "zemax_geometric_image_analysis",
        "zemax_ray_trace_diagnostics", "zemax_energy_budget", "zemax_ray_footprint", "zemax_diagnose_clipping"
    };

    public static bool UsesSharedRunner(string tool) => SharedRunner.Contains(tool);
    public static bool SupportsTasks(string tool) => UsesSharedRunner(tool) || NativeJobs.Contains(tool);
    public static IReadOnlyList<string> EligibleTools => SharedRunner.Concat(NativeJobs).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    public static bool IsBackgroundRequested(string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (!SupportsTasks(tool)) return false;
        if (tool == "zemax_multistart_optimize") return true;
        if (arguments != null && arguments.TryGetValue("runInBackground", out var value))
            return value.ValueKind == JsonValueKind.True;
        return NativeJobs.Contains(tool);
    }

    public static (JsonElement Arguments, bool Background, int TimeoutSeconds) PrepareSharedInvocation(string tool, JsonElement arguments)
    {
        if (!UsesSharedRunner(tool)) return (arguments, false, 300);
        if (arguments.ValueKind != JsonValueKind.Object) return (arguments, false, 300);
        var background = false;
        var timeout = 300;
        if (arguments.TryGetProperty("runInBackground", out var flag))
        {
            if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("runInBackground must be a boolean.");
            background = flag.GetBoolean();
        }
        if (arguments.TryGetProperty("backgroundTimeoutSeconds", out var deadline) &&
            (deadline.ValueKind != JsonValueKind.Number || !deadline.TryGetInt32(out timeout) || timeout < 1 || timeout > 7200))
            throw new ArgumentException("backgroundTimeoutSeconds must be an integer between 1 and 7200.");
        var clean = JsonNode.Parse(arguments.GetRawText())!.AsObject();
        clean.Remove("runInBackground");
        clean.Remove("backgroundTimeoutSeconds");
        using var document = JsonDocument.Parse(clean.ToJsonString());
        return (document.RootElement.Clone(), background, timeout);
    }
}
