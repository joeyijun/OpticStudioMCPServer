using System.ComponentModel;
using System.Text.Json;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.Catalog;

[ZemaxToolType]
public sealed class EngineeringWorkflowTool
{
    private readonly IZemaxSession _session;
    public EngineeringWorkflowTool(IZemaxSession session) => _session = session;
    [ZemaxTool(Name = "zemax_workflow_baseline")]
    [Description("Capture a read-only, Worker-local 15-minute Sequential editing baseline for selected surfaces. Return its ID to zemax_batch_set_surfaces(expectedBaselineId). Guard covers active identity, all basic LDE geometry and declared analysis conditions, NOT solves/coatings/aspheric parameters or the complete optical model. No edits, snapshots, tracing or saving.")]
    public async Task<object> BaselineAsync([Description("Unique existing LDE surface numbers intended for editing, 1..100 entries.")] int[] surfaceNumbers,
        CancellationToken cancellationToken = default)
    {
        if (surfaceNumbers == null || surfaceNumbers.Length is < 1 or > 100 || surfaceNumbers.Distinct().Count() != surfaceNumbers.Length)
            throw new ArgumentException("Provide 1..100 unique surface numbers.");
        return await _session.ExecuteAsync("WorkflowBaseline", null, system =>
        {
            if (surfaceNumbers.Any(i => i < 0 || i >= system.LDE.NumberOfSurfaces)) throw new ArgumentException("Target surface does not exist.");
            var state = EngineeringModelReader.Read(system, _session.ModelIdentity);
            var baseline = EngineeringBaselineStore.Register(surfaceNumbers, state);
            using var json = JsonDocument.Parse(state);
            return (object)new
            {
                success = true,
                baseline,
                conditions = EngineeringConditionReader.Read(system, _session.ModelIdentity),
                targetState = json.RootElement.GetProperty("rows").EnumerateArray().Where(r => surfaceNumbers.Contains(r.GetProperty("number").GetInt32())).Select(r => r.Clone()).ToArray(),
                nextSteps = new[] { "Record optical baseline analyses with fixed settings", "Review proposed edits", "Call zemax_batch_set_surfaces with expectedBaselineId", "Call zemax_workflow_verify", "Repeat identical analyses and apply explicit acceptance rules", "Save only after approval" }
            };
        }, cancellationToken);
    }
    [ZemaxTool(Name = "zemax_workflow_verify")]
    [Description("Independently reread a guarded Sequential workflow's model after an approved batch edit. Distinguish verified geometry from optical acceptance. Does not modify, analyze, restore or save a lens. Use zemax_compare_analyses for numerical acceptance under identical analysis conditions.")]
    public Task<object> VerifyAsync([Description("ID returned by zemax_workflow_baseline in this Worker.")] string baselineId,
        CancellationToken cancellationToken = default) => _session.ExecuteAsync("WorkflowVerify", null,
            system => EngineeringBaselineStore.Verify(baselineId, EngineeringModelReader.Read(system, _session.ModelIdentity)), cancellationToken);
}
