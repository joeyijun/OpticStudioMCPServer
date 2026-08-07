using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class SetCurrentConfigurationTool
{
    private readonly IZemaxSession _session;

    public SetCurrentConfigurationTool(IZemaxSession session) => _session = session;

    public record SetCurrentConfigurationResult(
        bool Success,
        string? Error,
        int CurrentConfiguration,
        int NumberOfConfigurations
    );

    [ZemaxTool(Name = "zemax_set_current_configuration")]
    [Description("Set the current active configuration and verify the value read back from the Multi-Configuration Editor.")]
    public async Task<SetCurrentConfigurationResult> ExecuteAsync(
        [Description("Configuration number to set as current (1-indexed)")] int configurationNumber,
        CancellationToken cancellationToken = default)
    {
        if (configurationNumber < 1)
            return new SetCurrentConfigurationResult(false, "Configuration number must be at least 1.", 0, 0);

        try
        {
            return await _session.ExecuteAsync("SetCurrentConfiguration",
                new Dictionary<string, object?> { ["configurationNumber"] = configurationNumber },
                system =>
                {
                    var mce = system.MCE;
                    if (configurationNumber > mce.NumberOfConfigurations)
                        throw new ArgumentOutOfRangeException(nameof(configurationNumber),
                            $"Configuration {configurationNumber} does not exist. System has {mce.NumberOfConfigurations} configurations.");
                    if (!mce.SetCurrentConfiguration(configurationNumber))
                        throw new InvalidOperationException($"OpticStudio rejected configuration {configurationNumber} as the active configuration.");
                    if (mce.CurrentConfiguration != configurationNumber)
                        throw new InvalidOperationException($"OpticStudio reported current configuration {mce.CurrentConfiguration} after requesting {configurationNumber}.");

                    return new SetCurrentConfigurationResult(true, null, mce.CurrentConfiguration, mce.NumberOfConfigurations);
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SetCurrentConfigurationResult(false, ex.Message, 0, 0);
        }
    }
}
