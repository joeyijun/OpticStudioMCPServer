using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZOSAPI.Tools.Optimization;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class GlobalSearchTool
{
    private readonly IZemaxSession _session;
    private readonly McpJobManager _jobs;

    public GlobalSearchTool(IZemaxSession session, McpJobManager jobs)
    {
        _session = session;
        _jobs = jobs;
    }

    public record GlobalSearchResult(
        bool Success,
        string? Error,
        double InitialMerit,
        double BestMerit,
        double Improvement,
        int SolutionsSaved,
        string Algorithm,
        double RuntimeSeconds,
        string TerminationReason,
        string? JobId = null,
        string? JobState = null
    );

    public record SolutionInfo(
        int SolutionNumber,
        double MeritFunction
    );

    [ZemaxTool(Name = "zemax_global_search")]
    [Description("Run global optimization on the current optical system. Supports glass substitution when surfaces have MaterialSubstitute solve set.")]
    public async Task<GlobalSearchResult> ExecuteAsync(
        [Description("Optimization algorithm: DLS or Orthogonal")] string algorithm = "DLS",
        [Description("Number of CPU cores to use (0 for all available)")] int cores = 0,
        [Description("Number of solutions to save: 10, 20, 50, or 100")] int solutionsToSave = 10,
        [Description("Maximum runtime in seconds (0 for no limit - will run until cancelled)")] double timeoutSeconds = 60,
        [Description("Queue this long operation and return a job id immediately (recommended)")] bool runInBackground = true)
    {
        if (!runInBackground) return await ExecuteCoreAsync(algorithm, cores, solutionsToSave, timeoutSeconds, CancellationToken.None);

        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds + 15) : (TimeSpan?)null;
        var job = _jobs.Enqueue("zemax_global_search", async context =>
        {
            context.ReportProgress(0, "Waiting for the ZOS-API job slot.");
            var result = await ExecuteCoreAsync(algorithm, cores, solutionsToSave, timeoutSeconds, context.CancellationToken);
            if (!result.Success) throw new InvalidOperationException(result.Error ?? "Global search failed.");
            context.SetResult(result);
            context.ReportProgress(1, result.TerminationReason);
        }, timeout);
        return new GlobalSearchResult(true, null, 0, 0, 0, 0, algorithm, 0,
            "Queued. Use zemax_job_status or zemax_job_cancel.", job.JobId, job.State.ToString());
    }

    private async Task<GlobalSearchResult> ExecuteCoreAsync(
        string algorithm, int cores, int solutionsToSave, double timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["algorithm"] = algorithm,
                ["cores"] = cores,
                ["solutionsToSave"] = solutionsToSave,
                ["timeoutSeconds"] = timeoutSeconds
            };

            cancellationToken.ThrowIfCancellationRequested();
            var result = await _session.ExecuteAsync("GlobalSearch", parameters, system =>
            {
                if (system == null)
                {
                    throw new InvalidOperationException("Optical system is not available");
                }

                var mfe = system.MFE;
                if (mfe == null)
                {
                    throw new InvalidOperationException("Merit Function Editor is not available");
                }

                var initialMerit = mfe.CalculateMeritFunction();

                var globalOpt = system.Tools?.OpenGlobalOptimization();
                if (globalOpt == null)
                {
                    throw new InvalidOperationException("Failed to open Global Optimization tool");
                }

                try
                {
                    // Set algorithm
                    globalOpt.Algorithm = algorithm.ToUpper() switch
                    {
                        "DLS" => OptimizationAlgorithm.DampedLeastSquares,
                        "ORTHOGONAL" => OptimizationAlgorithm.OrthogonalDescent,
                        _ => OptimizationAlgorithm.DampedLeastSquares
                    };

                    // Set number of cores (0 means use MaxCores)
                    if (cores > 0)
                    {
                        globalOpt.NumberOfCores = Math.Min(cores, globalOpt.MaxCores);
                    }
                    else
                    {
                        globalOpt.NumberOfCores = globalOpt.MaxCores;
                    }

                    // Set number of solutions to save
                    globalOpt.NumberToSave = solutionsToSave switch
                    {
                        <= 10 => OptimizationSaveCount.Save_10,
                        <= 20 => OptimizationSaveCount.Save_20,
                        <= 50 => OptimizationSaveCount.Save_50,
                        _ => OptimizationSaveCount.Save_100
                    };

                    string terminationReason;
                    double actualRuntime = timeoutSeconds;

                    if (timeoutSeconds > 0)
                    {
                        var runStatus = globalOpt.RunAndWaitWithTimeout(timeoutSeconds);
                        terminationReason = runStatus.ToString();
                    }
                    else
                    {
                        // Run without timeout (automatic termination)
                        globalOpt.RunAndWaitForCompletion();
                        terminationReason = "Completed";
                    }

                    // Get best merit function (solution 1 is the best)
                    var bestMerit = globalOpt.CurrentMeritFunction(1);

                    // Count how many valid solutions we have
                    int validSolutions = 0;
                    for (int i = 1; i <= solutionsToSave; i++)
                    {
                        var merit = globalOpt.CurrentMeritFunction(i);
                        if (merit > 0 && merit < double.MaxValue)
                        {
                            validSolutions++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    return new GlobalSearchResult(
                        Success: true,
                        Error: null,
                        InitialMerit: initialMerit,
                        BestMerit: bestMerit,
                        Improvement: initialMerit - bestMerit,
                        SolutionsSaved: validSolutions,
                        Algorithm: algorithm,
                        RuntimeSeconds: actualRuntime,
                        TerminationReason: terminationReason
                    );
                }
                finally
                {
                    globalOpt.Close();
                }
            }, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            return new GlobalSearchResult(false, ex.Message, 0, 0, 0, 0, algorithm, 0, "Error");
        }
    }
}
