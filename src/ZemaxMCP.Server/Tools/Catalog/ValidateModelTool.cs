using System.ComponentModel;
using System.Text.Json;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.Catalog;

/// <summary>Full model-preflight entrypoint. Reuses the existing serialized,
/// read-only system summary; no duplicated ZOS editor interaction.</summary>
[ZemaxToolType]
public sealed class ValidateModelTool
{
    private readonly SystemSummaryTool _summary;
    public ValidateModelTool(IZemaxSession session) => _summary=new SystemSummaryTool(session);

    public sealed record Result(bool Success,string? Error,
        ModelWorkflowValidator.Evaluation? Evaluation,
        SystemSummaryTool.Result? Summary);

    [ZemaxTool(Name="zemax_validate_model")]
    [Description("Read-only, purpose-aware active-model preflight for imaging/clipping/energy/straylight/model-review. Reuses a serialized bounded system summary; reports blockers and required checks. No tracing, file switching, lens changes or claim of numerical/physical model validity.")]
    public async Task<Result> ExecuteAsync(
        [Description("Purpose: imaging, clipping, energy, straylight, model-review.")] string purpose="model-review",
        CancellationToken cancellationToken=default)
    {
        if(purpose?.Trim().ToLowerInvariant() is not (
            "imaging" or "clipping" or "energy" or "straylight" or "model-review"))
            return new Result(false,"Unsupported purpose; choose imaging, clipping, energy, straylight or model-review.",null,null);
        var summary=await _summary.ExecuteAsync(cancellationToken:cancellationToken).ConfigureAwait(false);
        if(!summary.Success)
            return new Result(false,summary.Error,null,summary);
        try
        {
            var json=JsonSerializer.Serialize(summary,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var assessment=ModelWorkflowValidator.Assess(json,purpose);
            return new Result(true,null,assessment,summary);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)
        {
            return new Result(false,ex.Message,null,summary);
        }
    }
}
