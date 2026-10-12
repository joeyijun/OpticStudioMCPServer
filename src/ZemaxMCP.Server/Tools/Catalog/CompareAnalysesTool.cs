using System.ComponentModel;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.Catalog;

[ZemaxToolType]
public sealed class CompareAnalysesTool
{
    [ZemaxTool(Name = "zemax_compare_analyses")]
    [Description("Compare two Worker-recorded successful spot-diagram or FFT-MTF analysis IDs from structuredContent.engineering.analysisId. Require the same recorded model/conditions/sampling, and identical MTF field/frequency axes. Apply explicit numerical rules; never invent missing/non-finite metrics, compare different methods, authorize edits or save a lens. Other analyses carry provenance but are not yet supported by this numerical comparator.")]
    public object Execute([Description("Earlier analysis ID in this Worker.")] string baselineAnalysisId,
        [Description("Later analysis ID in this Worker.")] string currentAnalysisId,
        [Description("1..24 rules: path (/rmsSpotRadius or /fields/0/tangentialMtf/3), goal (decrease/increase/range), optional minimum/maximum and minimumChange. Units come from recorded evidence, not caller declarations.")] List<AnalysisEvidenceStore.Rule> rules)
    {
        try { return AnalysisEvidenceStore.Compare(baselineAnalysisId, currentAnalysisId, rules); }
        catch (Exception ex) { return new { success = false, error = ex.Message, criteriaPassed = (bool?)null, scope = "No numerical acceptance was established." }; }
    }
}
