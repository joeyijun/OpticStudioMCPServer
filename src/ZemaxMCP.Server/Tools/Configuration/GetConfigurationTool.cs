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
    [Description("Get the number of configurations and current configuration")]
    public async Task<GetConfigurationResult> ExecuteAsync()
    {
        try
        {
            var result = await _session.ExecuteAsync("GetConfiguration", null, system =>
            {
                var mce = system.MCE;
                return new GetConfigurationResult(
                    Success: true,
                    Error: null,
                    NumberOfConfigurations: mce.NumberOfConfigurations,
                    CurrentConfiguration: mce.CurrentConfiguration
                );
            });
            return result;
        }
        catch (Exception ex)
        {
            return new GetConfigurationResult(false, ex.Message, 0, 0);
        }
    }
}
