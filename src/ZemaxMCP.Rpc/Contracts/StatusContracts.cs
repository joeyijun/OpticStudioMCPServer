using System;
using System.Collections.Generic;

namespace ZemaxMCP.Rpc;

public sealed class WorkerStatus
{
    public bool ZosApiLoaded { get; set; }
    public bool Connected { get; set; }
    public string ConnectionMode { get; set; } = "unknown";
    public string? ZosApiAssembly { get; set; }
    public string? OpticStudioDataDirectory { get; set; }
    public string? CurrentLicenseStatus { get; set; }
    public string? LastLicenseStatus { get; set; }
    public bool? LicenseValidForApi { get; set; }
    public string? LastConnectionError { get; set; }
    public string? SnapshotDirectory { get; set; }
    public string? LastSnapshotPath { get; set; }
    public IReadOnlyList<WorkerJobStatus> Jobs { get; set; } = Array.Empty<WorkerJobStatus>();
}

public sealed class WorkerJobStatus
{
    public string JobId { get; set; } = string.Empty;
    public string ToolName { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public double? Fraction { get; set; }
    public int QueuePosition { get; set; }
    public string? Message { get; set; }
}
