using System.Security.Cryptography;
using System.Text;

namespace ZemaxMCP.Server.Tools.Catalog;

// Worker-local, bounded, expiring evidence; never persists optical data or COM objects.
public static class EngineeringBaselineStore
{
    public sealed record Baseline(string Id, DateTimeOffset ExpiresAt, int[] Targets, string StateHash, string Scope);
    private sealed class Entry(Baseline baseline) { public Baseline Baseline = baseline; public string? AfterHash; public string? Snapshot; }
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    public const string Scope = "Sequential active-model identity, configuration, basic LDE geometry/material/comments/stop and field geometry/normalization/vignetting, wavelength and aperture conditions. Not a complete model fingerprint: solves, coatings, aspheric parameters, MFE/MCE operands and external files are not covered.";
    public static Baseline Register(int[] targets, string state)
    {
        lock (Gate)
        {
            foreach (var id in Entries.Where(p => p.Value.Baseline.ExpiresAt <= DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) Entries.Remove(id);
            if (Entries.Count >= 128) Entries.Remove(Entries.OrderBy(p => p.Value.Baseline.ExpiresAt).First().Key);
            var value = new Baseline(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddMinutes(15), targets.ToArray(), Hash(state), Scope);
            Entries.Add(value.Id, new(value)); return value;
        }
    }
    public static Baseline Get(string id) { lock (Gate) return Find(id).Baseline; }
    private static Entry Find(string id)
    {
        if (id == null || id.Length != 32 || !Entries.TryGetValue(id, out var e)) throw new EngineeringBaselineException("baseline_unavailable", "Baseline not found in this Worker; capture a new baseline.");
        if (e.Baseline.ExpiresAt <= DateTimeOffset.UtcNow) { Entries.Remove(id); throw new EngineeringBaselineException("baseline_expired", "Baseline expired; capture a new baseline."); }
        return e;
    }
    public static void Validate(string id, int[] editedTargets, string currentState)
    {
        lock (Gate)
        {
            var e = Find(id);
            if (e.AfterHash != null || !editedTargets.All(t => e.Baseline.Targets.Contains(t)) || Hash(currentState) != e.Baseline.StateHash)
                throw new EngineeringBaselineException("model_conflict", "Baseline is consumed, targets changed, or guarded model data changed. No snapshot or write was performed. Capture a new baseline and review edits.");
        }
    }
    public static void RecordApplied(string id, string state, string? snapshot) { lock (Gate) { var e = Find(id); e.AfterHash = Hash(state); e.Snapshot = snapshot; } }
    public static object Verify(string id, string state)
    {
        lock (Gate)
        {
            var e = Find(id); var verified = e.AfterHash != null && e.AfterHash == Hash(state);
            return new
            {
                success = true,
                baselineId = id,
                editRecorded = e.AfterHash != null,
                readbackVerified = verified,
                workflowState = e.AfterHash == null ? "awaiting_edit" : verified ? "readback_verified" : "model_changed_after_edit",
                saveAuthorized = false,
                nextSteps = e.AfterHash == null
                    ? new[] { "Preview proposed edits against this baseline; nothing has been applied or verified yet.",
                        "Obtain approval before calling zemax_batch_set_surfaces with expectedBaselineId." }
                    : verified
                        ? new[] { "Compare before/after analyses using identical settings and explicit numerical criteria.",
                            "Review remaining design requirements; saving still requires separate approval." }
                        : new[] { "Do not infer success or save automatically: independently reread the current model.",
                            "Review the snapshot and capture a fresh baseline before further approved edits." },
                snapshot = e.Snapshot,
                scope = Scope,
                opticalAcceptance = "Not established by geometry readback; supply comparable analyses and explicit engineering limits."
            };
        }
    }
    private static string Hash(string value)
    {
        if (value.Length > 262144) throw new ArgumentException("Model evidence exceeds its bounded contract.");
        using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
    }
}

public sealed class EngineeringBaselineException : InvalidOperationException
{
    public string Code { get; }
    public EngineeringBaselineException(string code, string message) : base(code + ": " + message) => Code = code;
}
