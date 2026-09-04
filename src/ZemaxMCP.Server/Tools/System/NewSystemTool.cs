using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public class NewSystemTool
{
    private readonly IZemaxSession _session;

    public NewSystemTool(IZemaxSession session) => _session = session;

    public record NewSystemResult(
        bool Success,
        string? Error,
        int NumberOfSurfaces
    );

    [ZemaxTool(Name = "zemax_new_system")]
    [Description("Create a new blank optical system. The current system is protected by the normal high-impact snapshot policy before replacement.")]
    public async Task<NewSystemResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var created = await _session.NewSystemAsync(cancellationToken);
            if (!created)
                return new NewSystemResult(false, "OpticStudio did not create a new optical system.", 0);

            // NewSystemAsync already logged/protected the HighImpact mutation.
            // Read the resulting surface count under a read-only command instead
            // of incorrectly triggering a second NewSystem safety snapshot.
            var numberOfSurfaces = await _session.ExecuteAsync("GetSystem", null,
                system => system.LDE.NumberOfSurfaces, cancellationToken);

            return new NewSystemResult(
                Success: true,
                Error: null,
                NumberOfSurfaces: numberOfSurfaces
            );
        }
        catch (Exception ex)
        {
            return new NewSystemResult(
                Success: false,
                Error: ex.Message,
                NumberOfSurfaces: 0
            );
        }
    }
}
