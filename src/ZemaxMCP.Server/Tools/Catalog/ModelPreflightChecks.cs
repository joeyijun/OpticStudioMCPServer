using System.Text.Json;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>Conservative, bounded model-metadata preflight. Every finding
/// identifies its limited evidence; no physical ray-performance verdict.</summary>
public static class ModelPreflightChecks
{
    public sealed record Finding(string Severity,string Code,string Message,string NextAction);

    internal static IReadOnlyList<Finding> Evaluate(string summaryJson)
    {
        if(string.IsNullOrEmpty(summaryJson) || summaryJson.Length>96000)
            throw new ArgumentException("Expected bounded system-summary JSON <=96KB.");
        using var doc=JsonDocument.Parse(summaryJson);
        var data=doc.RootElement;
        if(data.ValueKind!=JsonValueKind.Object ||
           !data.TryGetProperty("success",out var ok) || ok.ValueKind!=JsonValueKind.True)
            throw new ArgumentException("Cannot validate a failed or malformed summary.");
        int Count(string property) =>
            data.TryGetProperty(property,out var x) && x.ValueKind==JsonValueKind.Number ?
                x.GetInt32():0;
        string Value(string property) =>
            data.TryGetProperty(property,out var x) && x.ValueKind==JsonValueKind.String ?
                x.GetString()??"":"";
        bool Flag(string property) =>
            data.TryGetProperty(property,out var x) && x.ValueKind==JsonValueKind.True;
        var findings=new List<Finding>();
        var mode=Value("mode");
        if(mode.Equals("Sequential",StringComparison.OrdinalIgnoreCase))
        {
            if(Count("numberOfSurfaces")<3)
                findings.Add(new("blocker","missing_optics","No intermediate LDE optical surface in this active configuration.",
                    "Inspect zemax_get_system and create or open a valid design."));
            if(Count("numberOfFields")<1)
                findings.Add(new("blocker","missing_fields","No configured field samples were reported.",
                    "Inspect field data before image-quality or clipping analysis."));
            if(data.TryGetProperty("apertureValue",out var aperture) &&
               aperture.ValueKind==JsonValueKind.Number && aperture.GetDouble()<=0)
                findings.Add(new("warning","invalid_aperture","Aperture value is nonpositive.",
                    "Inspect the pupil/aperture definition and trace feasibility."));
            if(Count("omittedSurfaces")>0)
                findings.Add(new("info","bounded_lde","A prioritized subset of LDE surfaces was returned.",
                    "Inspect omitted surfaces before claiming exhaustive validation."));
            if(data.TryGetProperty("keySurfaces",out var rows) && rows.ValueKind==JsonValueKind.Array &&
                rows.EnumerateArray().Any(row=>row.TryGetProperty("isCoordinateBreak",out var cb) &&
                    cb.ValueKind==JsonValueKind.True))
                findings.Add(new("info","local_coordinate_frame","Coordinate Break exists in sampled LDE rows.",
                    "Do not compare ray intercepts across surfaces without a verified coordinate transform."));
        }
        else if(mode.Equals("NonSequential",StringComparison.OrdinalIgnoreCase))
        {
            if(Count("numberOfNscObjects")==0)
                findings.Add(new("blocker","empty_nsc_scene","No NSC objects were reported.",
                    "Inspect zemax_nsc_scene_summary before ray tracing."));
            else
                findings.Add(new("info","nsc_detector_verification","Object count does not confirm detector or active sources.",
                    "Inspect NSC source and detector objects before tracing and normalizing flux."));
        }
        if(Count("numberOfWavelengths")<1)
            findings.Add(new("blocker","missing_wavelengths","No wavelength configuration is reported.",
                "Configure a valid wavelength before optical analysis."));
        if(Count("configurations")>1)
            findings.Add(new("info","multi_configuration","Only the selected active configuration is summarized.",
                "Review all relevant configurations before comparing complete designs."));
        if(Flag("needsSave") || string.IsNullOrWhiteSpace(Value("filePath")))
            findings.Add(new("warning","unsaved_or_unlocated","The active model is unsaved or has no durable file path.",
                "Save a verified baseline/snapshot before destructive changes."));
        return findings;
    }
}
