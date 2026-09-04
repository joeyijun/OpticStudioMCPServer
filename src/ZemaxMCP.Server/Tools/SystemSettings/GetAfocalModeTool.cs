using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[ZemaxToolType]
public class GetAfocalModeTool
{
    private readonly IZemaxSession _session;

    public GetAfocalModeTool(IZemaxSession session) => _session = session;

    public record GetAfocalModeResult(
        bool Success,
        string? Error,
        bool AfocalMode
    );

    [ZemaxTool(Name = "zemax_get_afocal_mode")]
    [Description("Get the afocal mode setting")]
    public async Task<GetAfocalModeResult> ExecuteAsync()
    {
        try
        {
            var result = await _session.ExecuteAsync("GetAfocalMode", null, system =>
            {
                return new GetAfocalModeResult(
                    Success: true,
                    Error: null,
                    AfocalMode: system.SystemData.Aperture.AFocalImageSpace
                );
            });
            return result;
        }
        catch (Exception ex)
        {
            return new GetAfocalModeResult(false, ex.Message, false);
        }
    }
}
