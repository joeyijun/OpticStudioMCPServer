using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZemaxMCP.Server.Tools.Catalog;

public static class AnalysisEvidenceStore
{
    public sealed record Rule(string Path, string Goal, double? Minimum = null, double? Maximum = null, double MinimumChange = 0);
    private sealed record Entry(string Tool, string Result, string Evidence, DateTimeOffset Expires);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Records = new(StringComparer.Ordinal);
    public static string? Record(string tool, string result, string evidence)
    {
        if (result.Length > 524288 || evidence.Length > 131072) return null;
        lock (Gate)
        {
            foreach (var expiredId in Records.Where(p => p.Value.Expires <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) Records.Remove(expiredId);
            if (Records.Count >= 128) Records.Remove(Records.OrderBy(p => p.Value.Expires).First().Key);
            var id = Guid.NewGuid().ToString("N"); Records.Add(id, new(tool, result, evidence, DateTimeOffset.UtcNow.AddMinutes(60))); return id;
        }
    }
    public static object Compare(string baselineId, string currentId, IReadOnlyList<Rule> rules)
    {
        if (rules == null || rules.Count is < 1 or > 24) throw new ArgumentException("Provide 1..24 explicit numeric acceptance rules.");
        Entry a, b; lock (Gate) { a = Get(baselineId); b = Get(currentId); }
        if (a.Tool != b.Tool || a.Tool is not ("zemax_spot_diagram" or "zemax_fft_mtf"))
            throw new AnalysisEvidenceException("unsupported_analysis_method",
                "Comparison supports identical spot-diagram or FFT-MTF tools only; no cross-method comparisons.",
                "Repeat both measurements with the same supported analysis tool.");
        using var ar = JsonDocument.Parse(a.Result); using var br = JsonDocument.Parse(b.Result);
        using var ae = JsonDocument.Parse(a.Evidence); using var be = JsonDocument.Parse(b.Evidence);
        var ac = Conditions(ae.RootElement); var bc = Conditions(be.RootElement);
        if (ac.GetRawText() != bc.GetRawText() || Params(ae.RootElement) != Params(be.RootElement))
            throw new AnalysisEvidenceException("analysis_conditions_conflict",
                "Model identity, file, units, configuration, fields, wavelengths, aperture or sampling differ. Results are not comparable within the recorded scope.",
                "Inspect engineering.operations[].conditions and requestedParameters, then rerun both analyses with identical selections and native settings.");
        if (!Success(ar.RootElement) || !Success(br.RootElement)) throw new AnalysisEvidenceException(
            "analysis_failed", "Failed analysis is not acceptance evidence.", "Resolve the original analysis error before recording replacement evidence.");
        if (a.Tool == "zemax_fft_mtf") VerifyAxes(ar.RootElement, br.RootElement);
        var rows = new List<object>(); var allPassed = true;
        foreach (var rule in rules)
        {
            var pointer = rule.Path ?? throw new ArgumentException("Metric path must be non-null.");
            if (pointer.Length > 128) throw new ArgumentException("Metric path must be bounded.");
            var spot = a.Tool == "zemax_spot_diagram" && pointer == "/rmsSpotRadius";
            var mtf = a.Tool == "zemax_fft_mtf" && Regex.IsMatch(pointer, @"^/fields/[0-9]+/(tangentialMtf|sagittalMtf)/[0-9]+$");
            if (!spot && !mtf) throw new ArgumentException("Unsupported metric path; use real RMS spot radius or a field's tangential/sagittal MTF sample.");
            if (!Finite(rule.MinimumChange) || rule.MinimumChange < 0 || rule.Minimum.HasValue && !Finite(rule.Minimum.Value) || rule.Maximum.HasValue && !Finite(rule.Maximum.Value) || rule.Minimum > rule.Maximum)
                throw new ArgumentException("Acceptance limits must be finite and ordered.");
            var before = Number(ar.RootElement, pointer); var after = Number(br.RootElement, pointer);
            var delta = after - before;
            if (!Finite(delta)) throw new AnalysisEvidenceException("non_finite_metric_change",
                "Metric subtraction overflowed; no numerical acceptance can be established.",
                "Inspect the original metrics and units; do not treat overflow as improvement.");
            var pass = rule.Goal switch
            {
                "decrease" => delta < 0 && -delta >= rule.MinimumChange,
                "increase" => delta > 0 && delta >= rule.MinimumChange,
                "range" when rule.Minimum.HasValue || rule.Maximum.HasValue => (!rule.Minimum.HasValue || after >= rule.Minimum.Value) && (!rule.Maximum.HasValue || after <= rule.Maximum.Value),
                _ => throw new ArgumentException("Goal must be decrease, increase or range (with a bound).")
            };
            allPassed &= pass;
            var indexParts = pointer.Split('/');
            JsonElement? mtfField = mtf ? ar.RootElement.GetProperty("fields")[int.Parse(indexParts[2])] : null;
            rows.Add(new
            {
                rule.Path,
                before,
                after,
                delta,
                criterion = new { goal = rule.Goal, minimum = rule.Minimum, maximum = rule.Maximum, minimumChange = rule.MinimumChange },
                unit = spot ? ac.GetProperty("lensUnit").GetString() : "dimensionless MTF",
                fieldNumber = mtfField?.GetProperty("fieldNumber").GetInt32(),
                spatialFrequency = mtfField?.GetProperty("frequencies")[int.Parse(indexParts[indexParts.Length - 1])].GetDouble(),
                spatialFrequencyUnit = mtf && ac.TryGetProperty("mtfFrequencyUnit", out var frequencyUnit) ? frequencyUnit.GetString() : null,
                passed = pass
            });
        }
        return new
        {
            success = true,
            criteriaPassed = allPassed,
            comparisonState = allPassed ? "selected_criteria_passed" : "selected_criteria_not_met",
            saveAuthorized = false,
            nextSteps = allPassed
                ? new[] { "Review remaining design requirements and unrecorded native settings.", "Verify current model readback; saving still requires separate approval." }
                : new[] { "Inspect failed metrics, their criteria, units and field/frequency labels.", "Review the intended edit and snapshot with the user; do not automatically save or restore." },
            tool = a.Tool,
            baselineAnalysisId = baselineId,
            currentAnalysisId = currentId,
            metrics = rows,
            conditions = ac.Clone(),
            scope = "Worker-recorded analyses and explicitly selected numerical criteria only; not full design validation. Unrecorded native analysis options, coatings, polarization and ray-aiming conditions are not certified. No automatic save/restore or authorization."
        };
    }
    private static Entry Get(string id) => id != null && id.Length == 32 && Records.TryGetValue(id, out var e) && e.Expires > DateTimeOffset.UtcNow ? e : throw new AnalysisEvidenceException(
        "analysis_evidence_unavailable", "Analysis evidence not found or expired in this Worker.",
        "Repeat the analysis on the current Worker and use the returned engineering.analysisId; do not reuse IDs after a restart.");
    private static bool Success(JsonElement j) => j.ValueKind == JsonValueKind.Object && j.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
    private static JsonElement Conditions(JsonElement evidence)
    {
        if (evidence.TryGetProperty("truncated", out var trunc) && trunc.ValueKind == JsonValueKind.True) throw new ArgumentException("Truncated provenance cannot certify acceptance.");
        if (evidence.GetProperty("operations").GetArrayLength() != 1) throw new ArgumentException("Acceptance requires exactly one recorded analysis operation.");
        var op = evidence.GetProperty("operations")[0];
        if (op.GetProperty("status").GetString() != "known") throw new ArgumentException("Analysis conditions are unavailable.");
        var c = op.GetProperty("conditions");
        if (c.GetProperty("omittedFields").GetInt32() != 0 || c.GetProperty("omittedWavelengths").GetInt32() != 0) throw new ArgumentException("Truncated conditions cannot certify comparison.");
        foreach (var field in c.GetProperty("fields").EnumerateArray())
            foreach (var name in new[] { "x", "y", "weight", "vdx", "vdy", "vcx", "vcy", "tan" })
                if (!field.TryGetProperty(name, out var factor) || factor.ValueKind != JsonValueKind.Number)
                    throw new AnalysisEvidenceException("analysis_evidence_incomplete", "Missing field/vignetting conditions cannot certify comparison.",
                        "Resolve the unavailable field metadata and repeat both analyses; do not assume zero vignetting.");
        if (!c.TryGetProperty("fieldNormalization", out var normalization) ||
            normalization.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(normalization.GetString()))
            throw new AnalysisEvidenceException("analysis_evidence_incomplete", "Field normalization is unknown.",
                "Inspect native field settings and repeat both analyses with recorded normalization.");
        foreach (var wave in c.GetProperty("wavelengths").EnumerateArray())
            if (wave.GetProperty("micrometers").ValueKind != JsonValueKind.Number || wave.GetProperty("micrometers").GetDouble() <= 0 || wave.GetProperty("weight").ValueKind != JsonValueKind.Number)
                throw new ArgumentException("Missing wavelength conditions cannot certify comparison.");
        return c;
    }
    private static string Params(JsonElement e)
    {
        var op = e.GetProperty("operations")[0]; var p = op.GetProperty("requestedParameters");
        var names = op.GetProperty("command").GetString() switch
        {
            "SpotDiagram" => new[] { "field", "wavelength", "rings" },
            "MTF" => new[] { "frequency", "wavelength", "sampling" },
            _ => throw new ArgumentException("Unsupported recorded analysis method.")
        };
        foreach (var name in names) if (!p.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) throw new ArgumentException("Sampling/selection evidence is incomplete.");
        return p.GetRawText();
    }
    private static double Number(JsonElement root, string pointer)
    {
        var value = root;
        foreach (var key in pointer.Split('/').Skip(1))
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (!int.TryParse(key, out var index) || index < 0 || index >= value.GetArrayLength())
                    throw new AnalysisEvidenceException("invalid_metric_path", "Metric sample index is outside the recorded result.",
                        "Inspect the actual result arrays and select an existing field/sample; do not guess indices.");
                value = value[index];
            }
            else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child)) value = child;
            else throw new AnalysisEvidenceException("metric_unavailable", "Requested metric is not present in the recorded result.",
                "Inspect the original analysis result and choose a supported available metric; never substitute zero.");
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !Finite(number)) throw new AnalysisEvidenceException(
            "metric_unavailable", "Missing/non-finite metric is not a zero or a passing result.",
            "Inspect the original result and rerun the supported analysis; never substitute zero.");
        return number;
    }
    private static bool Finite(double v) => !double.IsInfinity(v) && !double.IsNaN(v);
    private static void VerifyAxes(JsonElement a, JsonElement b)
    {
        var af = a.GetProperty("fields"); var bf = b.GetProperty("fields");
        if (af.GetArrayLength() != bf.GetArrayLength()) throw new AnalysisEvidenceException(
            "analysis_axes_conflict", "MTF field counts differ.", "Repeat both FFT-MTF analyses with identical fields and sampling.");
        for (var i = 0; i < af.GetArrayLength(); i++)
            if (af[i].GetProperty("fieldNumber").GetRawText() != bf[i].GetProperty("fieldNumber").GetRawText() ||
                af[i].GetProperty("frequencies").GetRawText() != bf[i].GetProperty("frequencies").GetRawText())
                throw new AnalysisEvidenceException("analysis_axes_conflict",
                    "MTF sampling axes differ; index-based acceptance would be misleading.",
                    "Repeat both FFT-MTF analyses with identical field/frequency axes; do not compare unrelated sample indices.");
    }
}
