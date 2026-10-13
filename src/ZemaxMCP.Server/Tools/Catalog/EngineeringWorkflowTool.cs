using System.ComponentModel;
using System.Text.Json;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Server.Tools.LensData;

namespace ZemaxMCP.Server.Tools.Catalog;

[ZemaxToolType]
public sealed class EngineeringWorkflowTool
{
    private readonly IZemaxSession _session;
    public EngineeringWorkflowTool(IZemaxSession session) => _session = session;
    [ZemaxTool(Name = "zemax_preview_surface_edits")]
    [Description("Read-only dry-run of proposed Sequential batch surface edits against a fresh workflow baseline. Returns requested before/after values and stop-change warnings; rejects stale/consumed baselines or undeclared targets. Does not consume the baseline, create a snapshot, modify or save the lens. Native solve/recalculation effects are not predicted. Apply only after approval via zemax_batch_set_surfaces using the same expectedBaselineId.")]
    public async Task<object> PreviewAsync(
        [Description("Fresh ID from zemax_workflow_baseline in this Worker.")] string baselineId,
        [Description("Same edits contract as zemax_batch_set_surfaces: 1..100 unique surfaces, omitted values remain unchanged.")] List<BatchSetSurfacesTool.SurfaceEdit> edits,
        CancellationToken cancellationToken = default)
    {
        try
        {
            BatchSetSurfacesTool.ValidateEdits(edits);
            return await _session.ExecuteAsync("PreviewSurfaceEdits", null, system =>
            {
                var state = EngineeringModelReader.Read(system, _session.ModelIdentity);
                EngineeringBaselineStore.Validate(baselineId, edits.Select(e => e.SurfaceNumber).ToArray(), state);
                var requested = JsonSerializer.SerializeToElement(edits, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return (object)new
                {
                    success = true,
                    dryRun = true,
                    baselineId,
                    baselineExpiresAt = EngineeringBaselineStore.Get(baselineId).ExpiresAt,
                    preview = EngineeringEditPreview.Build(state, requested),
                    requiresApproval = true,
                    saveAuthorized = false,
                    applyTool = "zemax_batch_set_surfaces",
                    nextSteps = new[] { "Review the requested differences and native side-effect warnings.",
                    "Obtain approval; pass this baselineId as expectedBaselineId to the batch edit.",
                    "Independently read back and verify, compare fixed-condition analyses, then request separate save approval." }
                };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new
            {
                success = false,
                dryRun = true,
                error = ex.Message,
                code = ex is EngineeringBaselineException baselineError ? baselineError.Code :
                ex is ArgumentException ? "invalid_preview_input" : "preview_unavailable",
                requiresApproval = true,
                saveAuthorized = false,
                nextSteps = new[] { ex is EngineeringBaselineException
                ? "Reread the active lens and targets, capture a fresh baseline, and review the proposed edits again."
                : "Resolve the stated input or native-model error; no preview or approval was established.",
                "Do not apply, retry edits or save automatically." }
            };
        }
    }
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
                saveAuthorized = false,
                baseline,
                conditions = EngineeringConditionReader.Read(system, _session.ModelIdentity),
                targetState = json.RootElement.GetProperty("rows").EnumerateArray().Where(r => surfaceNumbers.Contains(r.GetProperty("number").GetInt32())).Select(r => r.Clone()).ToArray(),
                nextSteps = new[] { "Record optical baseline analyses with fixed settings", "Call zemax_preview_surface_edits and review requested changes", "Obtain approval before calling zemax_batch_set_surfaces with expectedBaselineId", "Call zemax_workflow_verify", "Repeat identical analyses and apply explicit acceptance rules", "Save only after approval" }
            };
        }, cancellationToken);
    }
    [ZemaxTool(Name = "zemax_workflow_verify")]
    [Description("Independently reread a guarded Sequential workflow's model after an approved batch edit. Distinguish verified geometry from optical acceptance. Does not modify, analyze, restore or save a lens. Use zemax_compare_analyses for numerical acceptance under identical analysis conditions.")]
    public Task<object> VerifyAsync([Description("ID returned by zemax_workflow_baseline in this Worker.")] string baselineId,
        CancellationToken cancellationToken = default) => _session.ExecuteAsync("WorkflowVerify", null,
            system => EngineeringBaselineStore.Verify(baselineId, EngineeringModelReader.Read(system, _session.ModelIdentity)), cancellationToken);
}
