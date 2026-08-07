using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Services.ConstrainedOptimization;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public class SaveFileTool
{
    private readonly IZemaxSession _session;
    private readonly ConstraintStore _constraintStore;

    public SaveFileTool(IZemaxSession session, ConstraintStore constraintStore)
    {
        _session = session;
        _constraintStore = constraintStore;
    }

    public record SaveFileResult(
        bool Success,
        string? Error,
        string? FilePath,
        List<string>? Warnings = null
    );

    [ZemaxTool(Name = "zemax_save_file")]
    [Description("Save the current lens system to file. The Zemax file result is authoritative; a constraint-sidecar failure is returned as a warning rather than falsely reporting that the lens save failed.")]
    public async Task<SaveFileResult> ExecuteAsync(
        [Description("File path (optional, uses current file if not specified)")] string? filePath = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (filePath is not null)
            {
                if (string.IsNullOrWhiteSpace(filePath))
                    return new SaveFileResult(false, "File path cannot be blank. Omit it to save to the current file.", null);
                filePath = Path.GetFullPath(filePath);
            }

            var saved = await _session.SaveFileAsync(filePath, cancellationToken);
            var savedPath = _session.CurrentFilePath;
            if (!saved || string.IsNullOrWhiteSpace(savedPath) || !File.Exists(savedPath))
                return new SaveFileResult(false, "OpticStudio did not create the requested lens file.", savedPath);

            var warnings = new List<string>();
            try
            {
                _constraintStore.SaveToFile(savedPath);
            }
            catch (Exception ex)
            {
                warnings.Add("The Zemax lens file was saved successfully, but the optimization-constraint sidecar could not be saved: " + ex.Message);
            }

            return new SaveFileResult(
                Success: true,
                Error: null,
                FilePath: savedPath,
                Warnings: warnings.Count > 0 ? warnings : null
            );
        }
        catch (Exception ex)
        {
            return new SaveFileResult(
                Success: false,
                Error: ex.Message,
                FilePath: _session.CurrentFilePath
            );
        }
    }
}
