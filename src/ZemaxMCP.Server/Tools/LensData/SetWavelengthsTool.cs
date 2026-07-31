using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.LensData;

[McpServerToolType]
public class SetWavelengthsTool
{
    private readonly IZemaxSession _session;

    public SetWavelengthsTool(IZemaxSession session) => _session = session;

    public record WavelengthDefinition(double Wavelength, double Weight = 1.0);

    public record SetWavelengthsResult(
        bool Success,
        string? Error,
        int NumberOfWavelengths,
        List<Wavelength> Wavelengths
    );

    [McpServerTool(Name = "zemax_set_wavelengths")]
    [Description("Set wavelength values. Automatically adds wavelengths if needed.")]
    public async Task<SetWavelengthsResult> ExecuteAsync(
        [Description("Array of wavelength definitions [{wavelength (um), weight}]")] List<WavelengthDefinition> wavelengths,
        [Description("Primary wavelength number (1-indexed)")] int primaryWavelength = 1)
    {
        try
        {
            if (wavelengths == null || wavelengths.Count == 0)
                throw new ArgumentException("At least one wavelength is required.", nameof(wavelengths));
            if (primaryWavelength < 1 || primaryWavelength > wavelengths.Count)
                throw new ArgumentOutOfRangeException(nameof(primaryWavelength), $"Primary wavelength must be between 1 and {wavelengths.Count}.");
            for (var i = 0; i < wavelengths.Count; i++)
            {
                var definition = wavelengths[i];
                if (double.IsNaN(definition.Wavelength) || double.IsInfinity(definition.Wavelength) || definition.Wavelength <= 0)
                    throw new ArgumentException($"Wavelength {i + 1} must be finite and positive.", nameof(wavelengths));
                if (double.IsNaN(definition.Weight) || double.IsInfinity(definition.Weight) || definition.Weight < 0)
                    throw new ArgumentException($"Weight {i + 1} must be finite and non-negative.", nameof(wavelengths));
            }

            var parameters = new Dictionary<string, object?>
            {
                ["wavelengthCount"] = wavelengths.Count,
                ["primaryWavelength"] = primaryWavelength
            };

            var result = await _session.ExecuteAsync("SetWavelengths", parameters, system =>
            {
                var sysWaves = system.SystemData.Wavelengths;

                // Add wavelengths if needed
                while (sysWaves.NumberOfWavelengths < wavelengths.Count)
                {
                    sysWaves.AddWavelength(0.55, 1.0);
                }

                // Remove excess wavelengths if needed
                while (sysWaves.NumberOfWavelengths > wavelengths.Count)
                {
                    sysWaves.RemoveWavelength(sysWaves.NumberOfWavelengths);
                }

                // Configure all wavelengths
                for (int i = 0; i < wavelengths.Count; i++)
                {
                    var wl = sysWaves.GetWavelength(i + 1);
                    wl.Wavelength = wavelengths[i].Wavelength;
                    wl.Weight = wavelengths[i].Weight;
                }

                sysWaves.GetWavelength(primaryWavelength).MakePrimary();

                var resultWaves = new List<Wavelength>();
                for (int i = 0; i < wavelengths.Count; i++)
                {
                    var wl = sysWaves.GetWavelength(i + 1);
                    resultWaves.Add(new Wavelength
                    {
                        Number = i + 1,
                        Value = wl.Wavelength.Sanitize(),
                        Weight = wl.Weight.Sanitize(),
                        IsPrimary = wl.IsPrimary
                    });
                }

                return new SetWavelengthsResult(
                    Success: true,
                    Error: null,
                    NumberOfWavelengths: sysWaves.NumberOfWavelengths,
                    Wavelengths: resultWaves
                );
            });

            return result;
        }
        catch (Exception ex)
        {
            return new SetWavelengthsResult(false, ex.Message, 0, new List<Wavelength>());
        }
    }
}
