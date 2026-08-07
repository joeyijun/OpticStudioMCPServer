using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public sealed class QuickFocusTool
{
    private readonly IZemaxSession _session;
    public QuickFocusTool(IZemaxSession session) => _session = session;

    public record QuickFocusResult(bool Success, string? Error, string RunStatus, string Criterion, bool UseCentroid, double ImageThicknessBefore, double ImageThicknessAfter, bool NeedsSave);

    [ZemaxTool(Name = "zemax_quick_focus")]
    [Description("Run OpticStudio Quick Focus on a sequential system. This changes focus but does not save the file. The timeout applies when the installed OpticStudio exposes Quick Focus asynchronously.")]
    public async Task<QuickFocusResult> ExecuteAsync(
        [Description("Criterion: SpotSizeRadial, SpotSizeXOnly, SpotSizeYOnly, or RMSWavefront")] string criterion = "SpotSizeRadial",
        [Description("Use centroid reference for spot-size criteria")] bool useCentroid = true,
        [Description("Maximum run time in seconds (1-300)")] double timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsed = criterion.Trim().ToLowerInvariant() switch
            {
                "spotsizeradial" => ZOSAPI.Tools.General.QuickFocusCriterion.SpotSizeRadial,
                "spotsizexonly" => ZOSAPI.Tools.General.QuickFocusCriterion.SpotSizeXOnly,
                "spotsizeyonly" => ZOSAPI.Tools.General.QuickFocusCriterion.SpotSizeYOnly,
                "rmswavefront" => ZOSAPI.Tools.General.QuickFocusCriterion.RMSWavefront,
                _ => throw new ArgumentException("Criterion must be SpotSizeRadial, SpotSizeXOnly, SpotSizeYOnly, or RMSWavefront.")
            };
            if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 300)
                throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Timeout must be between 1 and 300 seconds.");
            return await _session.ExecuteAsync("QuickFocus", new Dictionary<string, object?>
            {
                ["criterion"] = criterion, ["useCentroid"] = useCentroid, ["timeoutSeconds"] = timeoutSeconds
            }, system =>
            {
                if (system.LDE.NumberOfSurfaces < 3)
                    throw new InvalidOperationException("Quick Focus requires at least one non-object, non-image surface.");
                var focusSurface = system.LDE.GetSurfaceAt(system.LDE.NumberOfSurfaces - 2);
                var before = focusSurface.Thickness;
                var tool = system.Tools.OpenQuickFocus();
                if (tool == null) throw new InvalidOperationException("OpticStudio did not open Quick Focus.");
                try
                {
                    tool.Criterion = parsed;
                    tool.UseCentroid = useCentroid;
                    var run = SystemToolRunner.Run(tool, timeoutSeconds);
                    return new QuickFocusResult(run.Success, run.Error, run.RunStatus, parsed.ToString(), useCentroid,
                        before.Sanitize(), focusSurface.Thickness.Sanitize(), system.NeedsSave);
                }
                finally { tool.Close(); }
            }, cancellationToken);
        }
        catch (Exception ex) { return new QuickFocusResult(false, ex.Message, "Failed", criterion, useCentroid, 0, 0, false); }
    }
}
