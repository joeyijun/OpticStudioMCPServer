using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public sealed class VignettingTool
{
    private readonly IZemaxSession _session;
    public VignettingTool(IZemaxSession session) => _session = session;

    public record FieldVignetting(int FieldNumber, double X, double Y, double VDX, double VDY, double VCX, double VCY, double TAN);
    public record VignettingResult(bool Success, string? Error, string Operation, string Normalization, IReadOnlyList<FieldVignetting> Fields, bool NeedsSave);

    [ZemaxTool(Name = "zemax_get_vignetting")]
    [Description("Read VDX, VDY, VCX, VCY, and TAN vignetting factors for every sequential field.")]
    public Task<VignettingResult> GetAsync(CancellationToken cancellationToken = default) => ExecuteAsync("read", cancellationToken);

    [ZemaxTool(Name = "zemax_set_vignetting")]
    [Description("Ask OpticStudio to calculate and set vignetting factors for all sequential fields. The file is not saved automatically.")]
    public Task<VignettingResult> SetAsync(CancellationToken cancellationToken = default) => ExecuteAsync("calculate", cancellationToken);

    [ZemaxTool(Name = "zemax_clear_vignetting")]
    [Description("Clear all sequential field vignetting factors. The file is not saved automatically.")]
    public Task<VignettingResult> ClearAsync(CancellationToken cancellationToken = default) => ExecuteAsync("clear", cancellationToken);

    private async Task<VignettingResult> ExecuteAsync(string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await _session.ExecuteAsync(operation == "read" ? "GetVignetting" : operation == "clear" ? "ClearVignetting" : "SetVignetting",
                new Dictionary<string, object?> { ["operation"] = operation }, system =>
                {
                    var fields = system.SystemData.Fields;
                    if (operation == "calculate") fields.SetVignetting();
                    else if (operation == "clear") fields.ClearVignetting();
                    var values = new List<FieldVignetting>();
                    for (var i = 1; i <= fields.NumberOfFields; i++)
                    {
                        var field = fields.GetField(i);
                        values.Add(new FieldVignetting(i, field.X.Sanitize(), field.Y.Sanitize(), field.VDX.Sanitize(), field.VDY.Sanitize(),
                            field.VCX.Sanitize(), field.VCY.Sanitize(), field.TAN.Sanitize()));
                    }
                    return new VignettingResult(true, null, operation, fields.Normalization.ToString(), values, system.NeedsSave);
                }, cancellationToken);
        }
        catch (Exception ex) { return new VignettingResult(false, ex.Message, operation, "", Array.Empty<FieldVignetting>(), false); }
    }
}
