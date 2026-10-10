using System.ComponentModel;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.Catalog;

[ZemaxToolType]
public sealed class ExplainResultTool
{
    public sealed record Result(bool Success,string? Error,
        OpticalResultInterpreter.Interpretation? Explanation);

    [ZemaxTool(Name="zemax_explain_result")]
    [Description("Explain a completed successful result JSON from zemax_energy_budget, zemax_ray_footprint, zemax_get_nsc_detector, zemax_nsc_energy_budget, completed zemax_run_nsc_ray_trace with sameTraceEnergy, native zemax_audit_native_zrd topology evidence, or zemax_system_summary. Emits grounded metrics, physical caveats and follow-up tools. No model access or fabricated physical interpretations.")]
    public Task<Result> ExecuteAsync(
        [Description("Exact supported MCP tool name that produced this result.")] string toolName,
        [Description("Full successful JSON result payload from that tool (max 128 KiB).")] string resultJson,
        CancellationToken cancellationToken=default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { return Task.FromResult(new Result(true,null,
            OpticalResultInterpreter.Explain(toolName,resultJson))); }
        catch(Exception ex) when(ex is ArgumentException or System.Text.Json.JsonException)
        { return Task.FromResult(new Result(false,ex.Message,null)); }
    }
}
