using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.SystemSettings;

[McpServerToolType]
public sealed class SystemMetadataTool
{
    private readonly IZemaxSession _session;
    public SystemMetadataTool(IZemaxSession session) => _session = session;

    public record MetadataResult(bool Success, string? Error, string Title, string Author, string Notes, bool NeedsSave);

    [McpServerTool(Name = "zemax_get_system_metadata")]
    [Description("Read the optical system title, author, notes, and unsaved-change state.")]
    public Task<MetadataResult> GetAsync() => ExecuteAsync("GetSystemMetadata", null, null, null);

    [McpServerTool(Name = "zemax_set_system_metadata")]
    [Description("Set one or more optical system metadata fields. Omitted fields are preserved; the file is not saved automatically.")]
    public Task<MetadataResult> SetAsync(
        [Description("New system title; omit to preserve it")] string? title = null,
        [Description("New author; omit to preserve it")] string? author = null,
        [Description("New notes; omit to preserve them")] string? notes = null) =>
        ExecuteAsync("SetSystemMetadata", title, author, notes);

    private async Task<MetadataResult> ExecuteAsync(string command, string? title, string? author, string? notes)
    {
        try
        {
            return await _session.ExecuteAsync(command, new Dictionary<string, object?>
            {
                ["title"] = title, ["author"] = author, ["notes"] = notes
            }, system =>
            {
                var data = system.SystemData.TitleNotes;
                if (title != null) data.Title = title;
                if (author != null) data.Author = author;
                if (notes != null) data.Notes = notes;
                return new MetadataResult(true, null, data.Title ?? "", data.Author ?? "", data.Notes ?? "", system.NeedsSave);
            });
        }
        catch (Exception ex) { return new MetadataResult(false, ex.Message, "", "", "", false); }
    }
}
