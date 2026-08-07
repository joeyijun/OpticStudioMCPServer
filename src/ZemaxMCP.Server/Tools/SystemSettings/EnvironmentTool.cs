using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[ZemaxToolType]
public sealed class EnvironmentTool
{
    private readonly IZemaxSession _session;
    public EnvironmentTool(IZemaxSession session) => _session = session;

    public record EnvironmentResult(bool Success, string? Error, double TemperatureCelsius, double PressureAtmospheres, bool AdjustIndexToEnvironment, bool NeedsSave);

    [ZemaxTool(Name = "zemax_get_environment")]
    [Description("Read system temperature, pressure, and whether refractive-index data is adjusted to the environment.")]
    public Task<EnvironmentResult> GetAsync() => ChangeAsync(null, null, null, "GetEnvironment");

    [ZemaxTool(Name = "zemax_set_environment")]
    [Description("Set system temperature (°C), pressure (atm), and/or refractive-index environment adjustment. Omitted values are preserved.")]
    public Task<EnvironmentResult> SetAsync(
        [Description("System temperature in degrees Celsius")] double? temperatureCelsius = null,
        [Description("System pressure in atmospheres; must be non-negative")] double? pressureAtmospheres = null,
        [Description("Adjust catalog refractive indices to the specified environment")] bool? adjustIndexToEnvironment = null) =>
        ChangeAsync(temperatureCelsius, pressureAtmospheres, adjustIndexToEnvironment, "SetEnvironment");

    private async Task<EnvironmentResult> ChangeAsync(double? temperature, double? pressure, bool? adjust, string command)
    {
        try
        {
            if (temperature.HasValue && (double.IsNaN(temperature.Value) || double.IsInfinity(temperature.Value))) throw new ArgumentException("Temperature must be finite.");
            if (pressure.HasValue && (double.IsNaN(pressure.Value) || double.IsInfinity(pressure.Value) || pressure.Value < 0)) throw new ArgumentException("Pressure must be finite and non-negative.");
            return await _session.ExecuteAsync(command, new Dictionary<string, object?>
            {
                ["temperatureCelsius"] = temperature, ["pressureAtmospheres"] = pressure, ["adjustIndexToEnvironment"] = adjust
            }, system =>
            {
                var data = system.SystemData.Environment;
                if (temperature.HasValue) data.Temperature = temperature.Value;
                if (pressure.HasValue) data.Pressure = pressure.Value;
                if (adjust.HasValue) data.AdjustIndexToEnvironment = adjust.Value;
                return new EnvironmentResult(true, null, data.Temperature.Sanitize(), data.Pressure.Sanitize(), data.AdjustIndexToEnvironment, system.NeedsSave);
            });
        }
        catch (Exception ex) { return new EnvironmentResult(false, ex.Message, 0, 0, false, false); }
    }
}
