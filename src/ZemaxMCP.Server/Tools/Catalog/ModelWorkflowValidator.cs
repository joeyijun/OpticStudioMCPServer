using System.Text.Json;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>Purpose-aware, evidence-limited metadata preflight. It is NOT
/// an assertion that ray traces, coating physics, or mechanical CAD are valid.</summary>
public static class ModelWorkflowValidator
{
    public sealed record Evaluation(string Purpose, bool ModeApplicable,
        bool NoIdentifiedMetadataBlockers,
        IReadOnlyList<ModelPreflightChecks.Finding> Findings,
        IReadOnlyList<string> RequiredNextChecks,
        string Interpretation);

    internal static Evaluation Assess(string successfulSummaryJson,string purpose)
    {
        var task=purpose?.Trim().ToLowerInvariant() ?? "";
        if(task is not ("imaging" or "clipping" or "energy" or "straylight" or "model-review"))
            throw new ArgumentException("purpose must be imaging, clipping, energy, straylight or model-review.");
        var findings=ModelPreflightChecks.Evaluate(successfulSummaryJson).ToList();
        using var document=JsonDocument.Parse(successfulSummaryJson);
        var summary=document.RootElement;
        var mode=summary.TryGetProperty("mode",out var v) && v.ValueKind==JsonValueKind.String ?
            v.GetString()??"" : "";
        var sequential=mode.Equals("Sequential",StringComparison.OrdinalIgnoreCase);
        var nsc=mode.Equals("NonSequential",StringComparison.OrdinalIgnoreCase);
        var compatible=task=="model-review" ? sequential || nsc :
            task=="energy" ? sequential || nsc :
            task=="straylight" ? nsc : sequential;
        if(!compatible)
            findings.Add(new ModelPreflightChecks.Finding("blocker","incompatible_mode",
                "Selected purpose is not compatible with the active optical model mode.",
                "Open an appropriate model using an explicitly authorized file operation; never convert modes automatically."));
        var actions=new List<string>();
        if(task is "imaging" or "clipping")
        {
            actions.Add("Trace test rays for each relevant field/wavelength before claiming valid image quality.");
            if(task=="clipping") actions.Add(
                "Check real aperture geometry and compare local ray footprints; coordinate-break models need a verified transform.");
        }
        else if(task=="energy")
        {
            actions.Add("Use a trace-consistent source spectrum and physical coating/detector data before any watt-level claim.");
            actions.Add(nsc ? "Check actual NSC source/detector objects and the same-trace launched power." :
                "Distinguish sampled pupil survival from source-to-detector energy collection.");
        }
        else if(task=="straylight")
        {
            actions.Add("Verify source-object IDs, detector-object IDs and detector-clearing policy before tracing.");
            actions.Add("Do not sum NSC flux from overlapping detectors as conserved total throughput.");
        }
        else actions.Add("Inspect full editor rows/configurations and record a baseline before making edits.");
        actions.AddRange(findings.Where(x=>x.Severity is "warning" or "blocker")
            .Select(x=>x.NextAction).Distinct(StringComparer.Ordinal));
        return new Evaluation(task,compatible,
            compatible && !findings.Any(x=>x.Severity=="blocker"),
            findings,actions.Distinct(StringComparer.Ordinal).Take(12).ToArray(),
            "Preflight inspects a BOUNDED metadata snapshot, not all surfaces, full CAD, real-ray numerical validity, COM licensing or calibrated radiometric power. NoDetectedMetadataBlockers does not authorize changes.");
    }
}
