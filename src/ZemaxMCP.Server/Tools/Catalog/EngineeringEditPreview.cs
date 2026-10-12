using System.Globalization;
using System.Text.Json;

namespace ZemaxMCP.Server.Tools.Catalog;

// Pure requested-value diff. It never predicts native solve/recalculation effects.
internal static class EngineeringEditPreview
{
    private static readonly HashSet<string> Geometry = new(StringComparer.Ordinal)
        { "radius", "thickness", "semiDiameter", "conic" };
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        { "radius", "thickness", "semiDiameter", "conic", "material", "comment", "isStop" };

    internal static object Build(string modelState, JsonElement edits)
    {
        if (modelState.Length > 262144 || edits.ValueKind != JsonValueKind.Array ||
            edits.GetArrayLength() is < 1 or > 100) throw new ArgumentException("Preview exceeds its bounded editing contract.");
        using var state = JsonDocument.Parse(modelState);
        var rows = state.RootElement.GetProperty("rows").EnumerateArray().ToDictionary(
            row => row.GetProperty("number").GetInt32(), row => row);
        var changes = new List<object>();
        var targets = new HashSet<int>();
        var stopChange = false;
        foreach (var edit in edits.EnumerateArray())
        {
            var target = edit.GetProperty("surfaceNumber").GetInt32();
            if (!targets.Add(target) || !rows.TryGetValue(target, out var row))
                throw new ArgumentException("Preview requires unique existing target surfaces.");
            foreach (var property in edit.EnumerateObject())
            {
                if (property.Name == "surfaceNumber") continue;
                if (!Allowed.Contains(property.Name)) throw new ArgumentException("Unknown surface edit property.");
                if (property.Value.ValueKind == JsonValueKind.Null) continue; // omission retains state
                var before = row.GetProperty(property.Name);
                string? previous, proposed;
                if (Geometry.Contains(property.Name))
                {
                    var number = property.Value.GetDouble();
                    if (double.IsNaN(number) || double.IsInfinity(number))
                        throw new ArgumentException("Proposed geometry must be finite.");
                    previous = before.GetString();
                    proposed = number.ToString("R", CultureInfo.InvariantCulture);
                }
                else if (property.Name == "isStop")
                {
                    previous = before.GetBoolean() ? "true" : "false";
                    proposed = property.Value.GetBoolean() ? "true" : "false";
                    stopChange |= previous != proposed;
                }
                else
                {
                    previous = before.GetString();
                    proposed = property.Value.GetString();
                    if ((proposed?.Length ?? 0) > 2048)
                        throw new ArgumentException("Preview strings exceed the guarded model's bounded contract.");
                }
                if (previous != proposed)
                    changes.Add(new
                    {
                        surfaceNumber = target,
                        property = property.Name,
                        before = previous,
                        requestedAfter = proposed
                    });
            }
        }
        return new
        {
            changedPropertyCount = changes.Count,
            changes,
            containsRequestedChanges = changes.Count > 0,
            units = state.RootElement.GetProperty("conditions").Clone(),
            warnings = stopChange
                ? new[] { "Changing a stop may implicitly change another row's stop state; inspect all stops after applying." }
                : Array.Empty<string>(),
            scope = "Requested values only, using invariant strings to preserve native infinity. No solve/material/recalculation effects are predicted; no edits, snapshots, tracing or saving."
        };
    }
}
