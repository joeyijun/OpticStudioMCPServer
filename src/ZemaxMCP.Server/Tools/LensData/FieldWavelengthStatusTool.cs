using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.LensData;

[McpServerToolType]
public sealed class FieldWavelengthStatusTool
{
    private readonly IZemaxSession _session;
    public FieldWavelengthStatusTool(IZemaxSession session) => _session = session;

    public record FieldStatus(int Number, double X, double Y, double Weight, string Comment, bool Ignore, bool IsActive,
        string XSolve, string YSolve, double VDX, double VDY, double VCX, double VCY, double TAN);
    public record FieldsResult(bool Success, string? Error, string FieldType, string Normalization,
        IReadOnlyList<FieldStatus> Fields);
    public record WavelengthStatus(int Number, double WavelengthMicrometers, double Weight, bool IsPrimary, bool IsActive);
    public record WavelengthsResult(bool Success, string? Error, int PrimaryWavelength,
        IReadOnlyList<WavelengthStatus> Wavelengths);

    [McpServerTool(Name = "zemax_get_field_settings")]
    [Description("Read complete sequential field settings, including type, normalization, comments, solves, activity, and vignetting factors.")]
    public async Task<FieldsResult> GetFieldsAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetFieldSettings", null, system =>
            {
                var fields = system.SystemData.Fields;
                var result = new List<FieldStatus>();
                for (var i = 1; i <= fields.NumberOfFields; i++)
                {
                    var field = fields.GetField(i);
                    result.Add(new FieldStatus(i, field.X.Sanitize(), field.Y.Sanitize(), field.Weight.Sanitize(),
                        field.Comment ?? "", field.Ignore, field.IsActive, field.XSolve.ToString(), field.YSolve.ToString(),
                        field.VDX.Sanitize(), field.VDY.Sanitize(), field.VCX.Sanitize(), field.VCY.Sanitize(), field.TAN.Sanitize()));
                }
                return new FieldsResult(true, null, fields.GetFieldType().ToString(), fields.Normalization.ToString(), result);
            });
        }
        catch (Exception ex) { return new FieldsResult(false, ex.Message, "", "", Array.Empty<FieldStatus>()); }
    }

    [McpServerTool(Name = "zemax_get_wavelength_settings")]
    [Description("Read all sequential wavelengths with their weights, active state, and the actual primary wavelength.")]
    public async Task<WavelengthsResult> GetWavelengthsAsync()
    {
        try
        {
            return await _session.ExecuteAsync("GetWavelengthSettings", null, system =>
            {
                var wavelengths = system.SystemData.Wavelengths;
                var primary = 0;
                var result = new List<WavelengthStatus>();
                for (var i = 1; i <= wavelengths.NumberOfWavelengths; i++)
                {
                    var wavelength = wavelengths.GetWavelength(i);
                    if (wavelength.IsPrimary) primary = i;
                    result.Add(new WavelengthStatus(i, wavelength.Wavelength.Sanitize(), wavelength.Weight.Sanitize(),
                        wavelength.IsPrimary, wavelength.IsActive));
                }
                return new WavelengthsResult(true, null, primary, result);
            });
        }
        catch (Exception ex) { return new WavelengthsResult(false, ex.Message, 0, Array.Empty<WavelengthStatus>()); }
    }
}
