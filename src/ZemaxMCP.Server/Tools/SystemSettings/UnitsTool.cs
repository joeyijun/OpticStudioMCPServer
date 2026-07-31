using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[McpServerToolType]
public sealed class UnitsTool
{
    private readonly IZemaxSession _session;
    public UnitsTool(IZemaxSession session) => _session = session;

    public record UnitsResult(bool Success, string? Error, string LensUnits, string AnalysisUnits, string AnalysisPrefix, string SourceUnits, string SourcePrefix, string MtfUnits, string AfocalModeUnits);

    [McpServerTool(Name = "zemax_get_units")]
    [Description("Read all System Explorer unit settings. Use zemax_scale_lens—not a direct unit assignment—to rescale a physical lens design.")]
    public async Task<UnitsResult> GetAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetUnits", null, system =>
            {
                var data = system.SystemData.Units;
                return new UnitsResult(true, null, data.LensUnits.ToString(), data.AnalysisUnits.ToString(), data.AnalysisUnitPrefix.ToString(),
                    data.SourceUnits.ToString(), data.SourceUnitPrefix.ToString(), data.MTFUnits.ToString(), data.AfocalModeUnits.ToString());
            });
        }
        catch (Exception ex) { return new UnitsResult(false, ex.Message, "", "", "", "", "", "", ""); }
    }
}
