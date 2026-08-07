using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public sealed class StopAndFirstOrderTool
{
    private readonly IZemaxSession _session;
    public StopAndFirstOrderTool(IZemaxSession session) => _session = session;

    public record StopResult(bool Success, string? Error, int StopSurface, string Comment, bool NeedsSave);
    public record FirstOrderResult(bool Success, string? Error, double EffectiveFocalLength, double ParaxialWorkingFNumber, double RealWorkingFNumber, double ParaxialImageHeight, double ParaxialMagnification);

    [ZemaxTool(Name = "zemax_get_stop_surface")]
    [Description("Read the sequential system stop-surface number and comment.")]
    public Task<StopResult> GetStopAsync() => ReadOrSetStopAsync(null);

    [ZemaxTool(Name = "zemax_set_stop_surface")]
    [Description("Set the sequential aperture stop to an existing non-object, non-image surface. The file is not saved automatically.")]
    public Task<StopResult> SetStopAsync([Description("Surface number to make the aperture stop")] int surfaceNumber) => ReadOrSetStopAsync(surfaceNumber);

    private async Task<StopResult> ReadOrSetStopAsync(int? surfaceNumber)
    {
        try
        {
            return await _session.ExecuteAsync(surfaceNumber.HasValue ? "SetStopSurface" : "GetStopSurface",
                new Dictionary<string, object?> { ["surfaceNumber"] = surfaceNumber }, system =>
                {
                    var lde = system.LDE;
                    if (surfaceNumber.HasValue)
                    {
                        if (surfaceNumber.Value < 1 || surfaceNumber.Value >= lde.NumberOfSurfaces - 1)
                            throw new ArgumentOutOfRangeException(nameof(surfaceNumber), $"Stop surface must be between 1 and {lde.NumberOfSurfaces - 2}.");
                        lde.GetSurfaceAt(surfaceNumber.Value).IsStop = true;
                    }
                    var stop = lde.StopSurface;
                    return new StopResult(true, null, stop, lde.GetSurfaceAt(stop).Comment ?? "", system.NeedsSave);
                });
        }
        catch (Exception ex) { return new StopResult(false, ex.Message, -1, "", false); }
    }

    [ZemaxTool(Name = "zemax_get_first_order_data")]
    [Description("Calculate first-order sequential data directly from the LDE: EFL, paraxial/real working F-number, paraxial image height, and magnification.")]
    public async Task<FirstOrderResult> GetFirstOrderAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetFirstOrderData", null, system =>
            {
                system.LDE.GetFirstOrderData(out var efl, out var paraxialFNumber, out var realFNumber, out var imageHeight, out var magnification);
                return new FirstOrderResult(true, null, efl.Sanitize(), paraxialFNumber.Sanitize(), realFNumber.Sanitize(), imageHeight.Sanitize(), magnification.Sanitize());
            });
        }
        catch (Exception ex) { return new FirstOrderResult(false, ex.Message, 0, 0, 0, 0, 0); }
    }
}
