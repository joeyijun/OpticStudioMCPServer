namespace ZemaxMCP.Server.Tools.Base;

internal static class SystemToolRunner
{
    internal record Result(bool Success, string RunStatus, string? Error);

    internal static Result Run(ZOSAPI.Tools.ISystemTool tool, double timeoutSeconds)
    {
        if (tool.IsAsynchronous)
        {
            var status = tool.RunAndWaitWithTimeout(timeoutSeconds);
            var success = status == ZOSAPI.Tools.RunStatus.Completed && tool.Succeeded;
            return new Result(success, status.ToString(), success ? null : GetError(tool, status.ToString()));
        }

        // OpticStudio 2024 R1 reports FailedToStart from RunAndWaitWithTimeout
        // for synchronous tools even though the operation is applied. Use the
        // synchronous API for these tools and trust its Boolean result.
        var completed = tool.RunAndWaitForCompletion();
        return new Result(completed, completed ? "Completed" : "Failed", completed ? null : GetError(tool, "The OpticStudio tool did not complete."));
    }

    private static string GetError(ZOSAPI.Tools.ISystemTool tool, string fallback) =>
        !string.IsNullOrWhiteSpace(tool.ErrorMessage) ? tool.ErrorMessage :
        !string.IsNullOrWhiteSpace(tool.Status) ? tool.Status : fallback;
}
