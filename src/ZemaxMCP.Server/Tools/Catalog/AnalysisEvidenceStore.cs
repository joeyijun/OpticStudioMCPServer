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
            throw new ArgumentException("Comparison supports identical spot-diagram or FFT-MTF tools only; no cross-method comparisons.");
        using var ar = JsonDocument.Parse(a.Result); using var br = JsonDocument.Parse(b.Result);
        using var ae = JsonDocument.Parse(a.Evidence); using var be = JsonDocument.Parse(b.Evidence);
        var ac = Conditions(ae.RootElement); var bc = Conditions(be.RootElement);
        if (ac.GetRawText() != bc.GetRawText() || Params(ae.RootElement) != Params(be.RootElement))
            throw new InvalidOperationException("analysis_conditions_conflict: model identity, file, units, configuration, fields, wavelengths, aperture or sampling differ. Results are not comparable within the recorded scope.");
        if (!Success(ar.RootElement) || !Success(br.RootElement)) throw new ArgumentException("Failed analysis is not acceptance evidence.");
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
            var pass = rule.Goal switch
            {
                "decrease" => after < before - rule.MinimumChange,
                "increase" => after > before + rule.MinimumChange,
                "range" when rule.Minimum.HasValue || rule.Maximum.HasValue => (!rule.Minimum.HasValue || after >= rule.Minimum.Value) && (!rule.Maximum.HasValue || after <= rule.Maximum.Value),
                _ => throw new ArgumentException("Goal must be decrease, increase or range (with a bound).")
            };
            allPassed &= pass;
            rows.Add(new { rule.Path, before, after, delta = after - before, unit = spot ? ac.GetProperty("lensUnit").GetString() : "dimensionless MTF", passed = pass });
        }
        return new
        {
            success = true,
            criteriaPassed = allPassed,
            tool = a.Tool,
            baselineAnalysisId = baselineId,
            currentAnalysisId = currentId,
            metrics = rows,
            conditions = ac.Clone(),
            scope = "Worker-recorded analyses and explicitly selected numerical criteria only; not full design validation. Unrecorded native analysis options, coatings, polarization and ray-aiming conditions are not certified. No automatic save/restore or authorization."
        };
    }
    private static Entry Get(string id) => id != null && id.Length == 32 && Records.TryGetValue(id, out var e) && e.Expires > DateTimeOffset.UtcNow ? e : throw new ArgumentException("Analysis evidence not found or expired in this Worker; repeat the analysis.");
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
            foreach (var name in new[] { "x", "y", "weight" })
                if (field.GetProperty(name).ValueKind != JsonValueKind.Number) throw new ArgumentException("Missing field conditions cannot certify comparison.");
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
        foreach (var key in pointer.Split('/').Skip(1)) value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(key)] : value.GetProperty(key);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !Finite(number)) throw new ArgumentException("Missing/non-finite metric is not a zero or a passing result.");
        return number;
    }
    private static bool Finite(double v) => !double.IsInfinity(v) && !double.IsNaN(v);
    private static void VerifyAxes(JsonElement a, JsonElement b)
    {
        var af = a.GetProperty("fields"); var bf = b.GetProperty("fields");
        if (af.GetArrayLength() != bf.GetArrayLength()) throw new ArgumentException("MTF field counts differ.");
        for (var i = 0; i < af.GetArrayLength(); i++)
            if (af[i].GetProperty("fieldNumber").GetRawText() != bf[i].GetProperty("fieldNumber").GetRawText() ||
                af[i].GetProperty("frequencies").GetRawText() != bf[i].GetProperty("frequencies").GetRawText())
                throw new ArgumentException("MTF sampling axes differ; index-based acceptance would be misleading.");
    }
}
