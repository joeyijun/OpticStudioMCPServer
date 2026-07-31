using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[McpServerToolType]
public sealed class PolarizationTool
{
    private readonly IZemaxSession _session;
    public PolarizationTool(IZemaxSession session) => _session = session;

    public record PolarizationResult(bool Success, string? Error, bool Unpolarized, double Jx, double Jy, double XPhaseDegrees, double YPhaseDegrees, string Method, bool ConvertThinFilmPhaseToRayEquivalent, bool NeedsSave);

    [McpServerTool(Name = "zemax_get_polarization")]
    [Description("Read the System Explorer polarization state, Jones amplitudes/phases, method, and thin-film phase option.")]
    public Task<PolarizationResult> GetAsync() => ChangeAsync(null, null, null, null, null, null, null, "GetPolarization");

    [McpServerTool(Name = "zemax_set_polarization")]
    [Description("Set one or more System Explorer polarization values. Omitted values are preserved.")]
    public Task<PolarizationResult> SetAsync(
        [Description("Use unpolarized light")] bool? unpolarized = null,
        [Description("Jones X amplitude; must be non-negative")] double? jx = null,
        [Description("Jones Y amplitude; must be non-negative")] double? jy = null,
        [Description("Jones X phase in degrees")] double? xPhaseDegrees = null,
        [Description("Jones Y phase in degrees")] double? yPhaseDegrees = null,
        [Description("Polarization method: XAxisMethod, YAxisMethod, or ZAxisMethod")] string? method = null,
        [Description("Convert thin-film phase to ray-equivalent phase")] bool? convertThinFilmPhaseToRayEquivalent = null) =>
        ChangeAsync(unpolarized, jx, jy, xPhaseDegrees, yPhaseDegrees, method, convertThinFilmPhaseToRayEquivalent, "SetPolarization");

    private async Task<PolarizationResult> ChangeAsync(bool? unpolarized, double? jx, double? jy, double? xPhase, double? yPhase, string? method, bool? convert, string command)
    {
        try
        {
            foreach (var value in new[] { jx, jy, xPhase, yPhase }.Where(x => x.HasValue))
                if (double.IsNaN(value!.Value) || double.IsInfinity(value.Value)) throw new ArgumentException("Polarization numeric values must be finite.");
            if (jx < 0 || jy < 0) throw new ArgumentException("Jones amplitudes must be non-negative.");
            ZOSAPI.SystemData.PolarizationMethod parsedMethod = default;
            if (method != null && !Enum.TryParse(method, true, out parsedMethod))
                throw new ArgumentException("Method must be XAxisMethod, YAxisMethod, or ZAxisMethod.");
            return await _session.ExecuteAsync(command, new Dictionary<string, object?>
            {
                ["unpolarized"] = unpolarized, ["jx"] = jx, ["jy"] = jy, ["xPhaseDegrees"] = xPhase,
                ["yPhaseDegrees"] = yPhase, ["method"] = method, ["convertThinFilmPhaseToRayEquivalent"] = convert
            }, system =>
            {
                var data = system.SystemData.Polarization;
                if (unpolarized.HasValue) data.Unpolarized = unpolarized.Value;
                if (jx.HasValue) data.Jx = jx.Value;
                if (jy.HasValue) data.Jy = jy.Value;
                if (xPhase.HasValue) data.XPhase = xPhase.Value;
                if (yPhase.HasValue) data.YPhase = yPhase.Value;
                if (method != null) data.Method = parsedMethod;
                if (convert.HasValue) data.ConvertThinFilmPhaseToRayEquivalent = convert.Value;
                return new PolarizationResult(true, null, data.Unpolarized, data.Jx.Sanitize(), data.Jy.Sanitize(), data.XPhase.Sanitize(), data.YPhase.Sanitize(),
                    data.Method.ToString(), data.ConvertThinFilmPhaseToRayEquivalent, system.NeedsSave);
            });
        }
        catch (Exception ex) { return new PolarizationResult(false, ex.Message, false, 0, 0, 0, 0, "", false, false); }
    }
}
