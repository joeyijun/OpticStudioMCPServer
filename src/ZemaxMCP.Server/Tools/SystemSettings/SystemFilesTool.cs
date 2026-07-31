using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[McpServerToolType]
public sealed class SystemFilesTool
{
    private readonly IZemaxSession _session;
    public SystemFilesTool(IZemaxSession session) => _session = session;

    public record SystemFilesResult(bool Success, string? Error, string CoatingFile, string ScatterProfile,
        string AbgDataFile, string GradiumProfile, IReadOnlyList<string> AvailableCoatingFiles,
        IReadOnlyList<string> AvailableScatterProfiles, IReadOnlyList<string> AvailableAbgDataFiles,
        IReadOnlyList<string> AvailableGradiumProfiles);

    [McpServerTool(Name = "zemax_get_system_files")]
    [Description("Read the coating, scatter, ABg, and GRIN data files selected by the optical system; optionally list the files available to OpticStudio.")]
    public async Task<SystemFilesResult> ExecuteAsync(
        [Description("Include available file lists; disabled by default to keep the MCP response compact")] bool includeAvailable = false)
    {
        try
        {
            return await _session.ExecuteAsync("GetSystemFiles", new Dictionary<string, object?>
            {
                ["includeAvailable"] = includeAvailable
            }, system =>
            {
                var files = system.SystemData.Files;
                return new SystemFilesResult(true, null, files.CoatingFile ?? "", files.ScatterProfile ?? "",
                    files.ABgDataFile ?? "", files.GradiumProfile ?? "",
                    includeAvailable ? files.GetCoatingFiles() : Array.Empty<string>(),
                    includeAvailable ? files.GetScatterProfiles() : Array.Empty<string>(),
                    includeAvailable ? files.GetABgDataFiles() : Array.Empty<string>(),
                    includeAvailable ? files.GetGradiumProfiles() : Array.Empty<string>());
            });
        }
        catch (Exception ex)
        {
            return new SystemFilesResult(false, ex.Message, "", "", "", "", Array.Empty<string>(),
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        }
    }
}
