using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public class OpenFileTool
{
    private readonly IZemaxSession _session;
    private readonly ConstraintStore _constraintStore;

    public OpenFileTool(IZemaxSession session, ConstraintStore constraintStore)
    {
        _session = session;
        _constraintStore = constraintStore;
    }

    public record OpenFileResult(
        bool Success,
        string? Error,
        string? FilePath,
        int NumberOfSurfaces,
        string? Title,
        int ConstraintsLoaded
    );

    [ZemaxTool(Name = "zemax_open_file")]
    [Description("Open a Zemax lens file (.zmx or .zos) and load any matching optimization-constraint sidecar.")]
    public async Task<OpenFileResult> ExecuteAsync(
        [Description("Full path to the lens file")] string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return new OpenFileResult(false, "File path is required.", null, 0, null, 0);

            filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
            {
                return new OpenFileResult(
                    Success: false,
                    Error: $"File not found: {filePath}",
                    FilePath: null,
                    NumberOfSurfaces: 0,
                    Title: null,
                    ConstraintsLoaded: 0
                );
            }

            var opened = await _session.OpenFileAsync(filePath, cancellationToken);
            if (!opened)
            {
                return new OpenFileResult(false, $"OpticStudio could not load the file: {filePath}", null, 0, null, 0);
            }

            // The load itself is already recorded by OpenFileAsync. Read the
            // resulting system under a read-only command so logs do not imply a
            // second open operation.
            var result = await _session.ExecuteAsync("GetSystem",
                new Dictionary<string, object?> { ["openedFilePath"] = filePath },
                system =>
            {
                _constraintStore.Clear();
                var systemFile = system.SystemFile;
                var constraintsLoaded = !string.IsNullOrEmpty(systemFile)
                    ? _constraintStore.LoadFromFile(systemFile)
                    : 0;

                return new OpenFileResult(
                    Success: true,
                    Error: null,
                    FilePath: systemFile,
                    NumberOfSurfaces: system.LDE.NumberOfSurfaces,
                    Title: system.SystemData.TitleNotes.Title ?? Path.GetFileNameWithoutExtension(systemFile),
                    ConstraintsLoaded: constraintsLoaded
                );
            }, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            return new OpenFileResult(
                Success: false,
                Error: ex.Message,
                FilePath: null,
                NumberOfSurfaces: 0,
                Title: null,
                ConstraintsLoaded: 0
            );
        }
    }
}
