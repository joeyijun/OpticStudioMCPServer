using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class SaveMeritFunctionFileTool
{
    private readonly IZemaxSession _session;

    public SaveMeritFunctionFileTool(IZemaxSession session) => _session = session;

    public record SaveMeritFunctionFileResult(
        bool Success,
        string? Error,
        string? FilePath,
        int NumberOfOperands);

    [ZemaxTool(Name = "zemax_save_merit_function_file")]
    [Description("Save the current merit function to an .MF file using an atomic final move/replace. Existing files are preserved unless overwrite=true.")]
    public async Task<SaveMeritFunctionFileResult> ExecuteAsync(
        [Description("Full path to save the merit function file. The path must end in .MF.")]
        string filePath,
        [Description("Allow replacement of an existing .MF file")]
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        string? fullPath = null;
        try
        {
            fullPath = ValidateOutputPath(filePath);
            if (!overwrite && File.Exists(fullPath))
                return new SaveMeritFunctionFileResult(false, $"File already exists: {fullPath}. Set overwrite=true to replace it.", null, 0);

            return await _session.ExecuteAsync("SaveMeritFunctionFile",
                new Dictionary<string, object?>
                {
                    ["filePath"] = fullPath,
                    ["overwrite"] = overwrite
                },
                system =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
                    var numberOfOperands = mfe.NumberOfOperands;
                    var directory = Path.GetDirectoryName(fullPath)!;
                    var tempPath = Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(fullPath)}.{Guid.NewGuid():N}.tmp.MF");

                    try
                    {
                        mfe.SaveMeritFunction(tempPath);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!File.Exists(tempPath))
                            throw new IOException("OpticStudio returned from SaveMeritFunction but the temporary .MF file was not created.");

                        if (overwrite)
                        {
                            if (File.Exists(fullPath))
                                File.Replace(tempPath, fullPath, null);
                            else
                                File.Move(tempPath, fullPath);
                        }
                        else
                        {
                            // File.Move is the final no-clobber check and closes the TOCTOU window.
                            File.Move(tempPath, fullPath);
                        }

                        if (!File.Exists(fullPath))
                            throw new IOException("The merit function file was not present after the final atomic move/replace.");

                        return new SaveMeritFunctionFileResult(true, null, fullPath, numberOfOperands);
                    }
                    finally
                    {
                        if (File.Exists(tempPath))
                            File.Delete(tempPath);
                    }
                }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SaveMeritFunctionFileResult(false, ex.Message, null, 0);
        }
    }

    private static string ValidateOutputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath.Trim());
        if (!string.Equals(Path.GetExtension(fullPath), ".MF", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Merit function output path must end in .MF; the tool does not silently change the requested path.", nameof(filePath));

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Output directory does not exist: {directory}");

        return fullPath;
    }
}
