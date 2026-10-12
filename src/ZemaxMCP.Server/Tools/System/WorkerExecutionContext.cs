using ZemaxMCP.ToolManifest;

namespace ZemaxMCP.Server.Tools.System;

public sealed record WorkerExecutionContext(
    string ExecutionComputer, string PathNamespace, string OperatingSystem,
    bool ClientComputerKnown, bool AutomaticFileTransfer,
    string? ZemaxDataDirectory, string? SamplesDirectory, string? LensDirectory,
    string? CurrentLensDirectory, string? CurrentLensFile, string? ConnectionMode,
    string Guidance)
{
    internal static WorkerExecutionContext Create(string? dataDirectory, string? currentFile, string? mode) => new(
        Environment.MachineName, ExecutionLocationPolicy.PathNamespace, "Windows",
        false, false, dataDirectory,
        string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, "Samples"),
        string.IsNullOrWhiteSpace(dataDirectory) ? null : Path.Combine(dataDirectory, "Lens"),
        string.IsNullOrWhiteSpace(currentFile) ? null : Path.GetDirectoryName(currentFile),
        currentFile, mode, ExecutionLocationPolicy.Instructions);
}
