using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public sealed class ScaleLensTool
{
    private readonly IZemaxSession _session;
    public ScaleLensTool(IZemaxSession session) => _session = session;

    public record ScaleLensResult(bool Success, string? Error, string RunStatus, string PreviousUnits, string CurrentUnits, double? Factor, int FirstComponent, int LastComponent, bool NeedsSave);

    [ZemaxTool(Name = "zemax_scale_lens")]
    [Description("Run OpticStudio Scale Lens by a positive factor or convert the complete sequential lens to Millimeters, Centimeters, Inches, or Meters. Provide exactly one of factor or targetUnits. The file is not saved automatically; the timeout applies to asynchronous implementations.")]
    public async Task<ScaleLensResult> ExecuteAsync(
        [Description("Positive geometric scale factor; mutually exclusive with targetUnits")] double? factor = null,
        [Description("Target units: Millimeters, Centimeters, Inches, or Meters; mutually exclusive with factor")] string? targetUnits = null,
        [Description("Maximum run time in seconds (1-300)")] double timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (factor.HasValue == !string.IsNullOrWhiteSpace(targetUnits))
                throw new ArgumentException("Provide exactly one of factor or targetUnits.");
            if (factor.HasValue && (double.IsNaN(factor.Value) || double.IsInfinity(factor.Value) || factor.Value <= 0))
                throw new ArgumentOutOfRangeException(nameof(factor), "Scale factor must be finite and positive.");
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 300)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 300 seconds.");

            ZOSAPI.Tools.General.ScaleToUnits parsedUnits = default;
            if (!string.IsNullOrWhiteSpace(targetUnits))
            {
                parsedUnits = targetUnits.Trim().ToLowerInvariant() switch
                {
                    "millimeters" => ZOSAPI.Tools.General.ScaleToUnits.Millimeters,
                    "centimeters" => ZOSAPI.Tools.General.ScaleToUnits.Centimeters,
                    "inches" => ZOSAPI.Tools.General.ScaleToUnits.Inches,
                    "meters" => ZOSAPI.Tools.General.ScaleToUnits.Meters,
                    _ => throw new ArgumentException("Target units must be Millimeters, Centimeters, Inches, or Meters.")
                };
            }

            return await _session.ExecuteAsync("ScaleLens", new Dictionary<string, object?>
            {
                ["factor"] = factor, ["targetUnits"] = targetUnits, ["timeoutSeconds"] = timeoutSeconds
            }, system =>
            {
                var previousUnits = system.SystemData.Units.LensUnits.ToString();
                var tool = system.Tools.OpenScale();
                if (tool == null) throw new InvalidOperationException("OpticStudio did not open Scale Lens.");
                try
                {
                    tool.FirstComponent = 1;
                    tool.LastComponent = tool.NumberOfComponents;
                    if (factor.HasValue)
                    {
                        tool.ScaleByFactor = true;
                        tool.ScaleByUnits = false;
                        tool.ScaleFactor = factor.Value;
                    }
                    else
                    {
                        tool.ScaleByFactor = false;
                        tool.ScaleByUnits = true;
                        tool.ScaleToUnit = parsedUnits;
                    }
                    var run = SystemToolRunner.Run(tool, timeoutSeconds);
                    return new ScaleLensResult(run.Success, run.Error, run.RunStatus, previousUnits,
                        system.SystemData.Units.LensUnits.ToString(), factor, tool.FirstComponent, tool.LastComponent, system.NeedsSave);
                }
                finally { tool.Close(); }
            }, cancellationToken);
        }
        catch (Exception ex) { return new ScaleLensResult(false, ex.Message, "Failed", "", "", factor, 0, 0, false); }
    }
}
