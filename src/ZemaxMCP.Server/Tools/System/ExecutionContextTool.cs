using System.ComponentModel;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tooling;

namespace ZemaxMCP.Server.Tools.System;

[ZemaxToolType]
public sealed class ExecutionContextTool
{
    private readonly IZemaxSession _session;
    public ExecutionContextTool(IZemaxSession session) => _session = session;

    [ZemaxTool(Name = "zemax_execution_context")]
    [Description("Identify the OpticStudio execution computer, server-side directory roots and current lens before locating files. Does not connect, switch a lens or take optical control. The AI client computer is not inferred.")]
    public Task<WorkerExecutionContext> ExecuteAsync() => Task.FromResult(
        WorkerExecutionContext.Create(_session.ZemaxDataDir, _session.CurrentFilePath, _session.CurrentMode?.ToString()));
}
