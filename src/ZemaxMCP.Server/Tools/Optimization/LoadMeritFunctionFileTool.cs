using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class LoadMeritFunctionFileTool
{
    private readonly IZemaxSession _session;

    public LoadMeritFunctionFileTool(IZemaxSession session) => _session = session;

    public record LoadMeritFunctionFileResult(
        bool Success,
        string? Error,
        string? FilePath,
        int NumberOfOperands,
        double? InitialMerit);

    [ZemaxTool(Name = "zemax_load_merit_function_file")]
    [Description("Replace the current Merit Function Editor from an .MF file. The existing MFE is backed up and restored if loading, validation, calculation, or cancellation fails.")]
    public async Task<LoadMeritFunctionFileResult> ExecuteAsync(
        [Description("Full path to the .MF merit function file to load")]
        string filePath,
        CancellationToken cancellationToken = default)
    {
        string? fullPath = null;
        try
        {
            fullPath = ValidateInputPath(filePath);

            return await _session.ExecuteAsync("LoadMeritFunctionFile",
                new Dictionary<string, object?> { ["filePath"] = fullPath },
                system =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
                    var backupPath = Path.Combine(Path.GetTempPath(), $"ZemaxMCP_MFE_Backup_{Guid.NewGuid():N}.MF");
                    var backupCreated = false;
                    try
                    {
                        mfe.SaveMeritFunction(backupPath);
                        backupCreated = File.Exists(backupPath);
                        if (!backupCreated)
                            throw new IOException("Unable to create a temporary Merit Function Editor rollback file.");

                        cancellationToken.ThrowIfCancellationRequested();
                        mfe.LoadMeritFunction(fullPath);
                        cancellationToken.ThrowIfCancellationRequested();

                        var numberOfOperands = mfe.NumberOfOperands;
                        if (numberOfOperands < 1)
                            throw new InvalidDataException("Loaded merit function contains no operands.");

                        var initialMerit = mfe.CalculateMeritFunction();
                        cancellationToken.ThrowIfCancellationRequested();
                        if (double.IsNaN(initialMerit) || double.IsInfinity(initialMerit))
                            throw new InvalidDataException("Loaded merit function produced a non-finite merit value.");

                        return new LoadMeritFunctionFileResult(
                            Success: true,
                            Error: null,
                            FilePath: fullPath,
                            NumberOfOperands: numberOfOperands,
                            InitialMerit: initialMerit);
                    }
                    catch
                    {
                        if (backupCreated)
                        {
                            try
                            {
                                mfe.LoadMeritFunction(backupPath);
                            }
                            catch (Exception rollbackException)
                            {
                                throw new InvalidOperationException(
                                    "Loading the requested merit function failed and restoring the original MFE also failed. Use the pre-operation safety snapshot for recovery.",
                                    rollbackException);
                            }
                        }
                        throw;
                    }
                    finally
                    {
                        if (File.Exists(backupPath))
                            File.Delete(backupPath);
                    }
                }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new LoadMeritFunctionFileResult(false, ex.Message, null, 0, null);
        }
    }

    private static string ValidateInputPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var fullPath = Path.GetFullPath(filePath.Trim());
        if (!string.Equals(Path.GetExtension(fullPath), ".MF", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Merit function input path must end in .MF.", nameof(filePath));
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Merit function file was not found.", fullPath);

        return fullPath;
    }
}
