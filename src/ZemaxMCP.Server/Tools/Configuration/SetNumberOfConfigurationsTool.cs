using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Configuration;

[ZemaxToolType]
public class SetNumberOfConfigurationsTool
{
    private readonly IZemaxSession _session;

    public SetNumberOfConfigurationsTool(IZemaxSession session) => _session = session;

    public record SetNumberOfConfigurationsResult(
        bool Success,
        string? Error,
        int NumberOfConfigurations
    );

    [ZemaxTool(Name = "zemax_set_number_of_configurations")]
    [Description("Set the number of configurations in the Multi-Configuration Editor. Existing configurations are removed from the end when reducing the count.")]
    public async Task<SetNumberOfConfigurationsResult> ExecuteAsync(
        [Description("Target number of configurations; must be at least 1.")] int numberOfConfigurations,
        CancellationToken cancellationToken = default)
    {
        if (numberOfConfigurations < 1)
            return new SetNumberOfConfigurationsResult(false, "Number of configurations must be at least 1.", 0);

        try
        {
            return await _session.ExecuteAsync("SetNumberOfConfigurations",
                new Dictionary<string, object?> { ["numberOfConfigurations"] = numberOfConfigurations },
                system =>
                {
                    var mce = system.MCE;
                    int currentCount = mce.NumberOfConfigurations;

                    if (numberOfConfigurations > currentCount)
                    {
                        for (int i = currentCount; i < numberOfConfigurations; i++)
                        {
                            if (!mce.AddConfiguration(false))
                                throw new InvalidOperationException($"OpticStudio failed while adding configuration {i + 1}; current count is {mce.NumberOfConfigurations}.");
                        }
                    }
                    else if (numberOfConfigurations < currentCount)
                    {
                        for (int i = currentCount; i > numberOfConfigurations; i--)
                        {
                            if (!mce.DeleteConfiguration(i))
                                throw new InvalidOperationException($"OpticStudio failed while deleting configuration {i}; current count is {mce.NumberOfConfigurations}.");
                        }
                    }

                    if (mce.NumberOfConfigurations != numberOfConfigurations)
                        throw new InvalidOperationException($"MCE configuration count is {mce.NumberOfConfigurations}; expected {numberOfConfigurations}.");

                    return new SetNumberOfConfigurationsResult(true, null, mce.NumberOfConfigurations);
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            return new SetNumberOfConfigurationsResult(false, ex.Message, 0);
        }
    }
}
