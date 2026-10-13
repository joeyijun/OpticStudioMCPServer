using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public sealed class ListLensFilesTool
{
    private readonly IZemaxSession _session;
    public ListLensFilesTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_list_lens_files")]
    [Description("Browse lens-file names on the OpticStudio computer, never the AI computer. Restricted to configured samples/lenses or the current lens directory. Single-directory, bounded metadata only; no file contents, arbitrary roots, uploads or link traversal.")]
    public Task<object> ExecuteAsync(
        [Description("Server root alias: samples (Zemax Data/Samples), lenses (Zemax Data/Lens), current (active lens directory).")] string root = "samples",
        [Description("Relative subdirectory within that server root; empty lists its top level. Absolute paths, '..', wildcard and link traversal are rejected.")] string relativeDirectory = "",
        [Description("Maximum returned file/directory entries (1-200). Truncated results are not a complete inventory.")] int maxEntries = 100,
        CancellationToken cancellationToken = default) => Task.FromResult<object>(
            ServerLensBrowser.List(root, relativeDirectory, maxEntries,
                _session.ZemaxDataDir, _session.CurrentFilePath, cancellationToken));
}
