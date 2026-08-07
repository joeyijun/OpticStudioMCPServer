using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class GetConfigurationTool
{
    private readonly IZemaxSession _session;

    public GetConfigurationTool(IZemaxSession session) => _session = session;

    public record GetConfigurationResult(
        bool Success,
        string? Error,
        int NumberOfConfigurations,
        int CurrentConfiguration
    );

    [ZemaxTool(Name = "zemax_get_configuration")]
    [Description("Get the number of MCE configurations and the current active configuration.")]
    public async Task<GetConfigurationResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _session.ExecuteAsync("GetConfiguration", null, system =>
            {
                var mce = system.MCE;
                return new GetConfigurationResult(true, null, mce.NumberOfConfigurations, mce.CurrentConfiguration);
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new GetConfigurationResult(false, ex.Message, 0, 0);
        }
    }
}
