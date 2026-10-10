using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZemaxMCP.Rpc;

namespace ZemaxMCP.HttpBridge.ModernHost;

/// <summary>
/// Stateless owner-filtered Job/Task state snapshots backed by Host memory.
/// Never asks the COM-bound Worker for status; a client cursor cannot reveal
/// other owners' progress, event count, task IDs or optical result data.
/// </summary>
internal sealed class McpJobTaskDeltaMonitor
{
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record JobView(string JobId, string ToolName, string State,
        double? Fraction, int QueuePosition, string? Message, double? ElapsedSeconds);

    internal sealed record Snapshot(long WorkerGeneration, bool OwnerScoped, bool WorkerBusy, bool StatusAvailable,
        IReadOnlyList<JobView> Jobs, IReadOnlyList<object> Tasks);

    internal sealed record Delta(string Cursor, bool Changed, Snapshot? Snapshot);

    internal static IReadOnlyList<WorkerJobStatus> MergeStatuses(
        IReadOnlyList<WorkerJobStatus> cached, IReadOnlyList<WorkerJobStatus> observed)
    {
        static bool Terminal(string state) =>
            state.Equals("Completed",StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Cancelled",StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Failed",StringComparison.OrdinalIgnoreCase);
        // Idempotent over duplicate cached/observed records: malformed
        // duplicate IDs do not crash the diagnostic endpoint.
        var merged=new Dictionary<string,WorkerJobStatus>(StringComparer.Ordinal);
        foreach(var job in cached)
            if(!string.IsNullOrWhiteSpace(job.JobId)) merged[job.JobId]=job;
        foreach(var eventStatus in observed)
        {
            if(string.IsNullOrWhiteSpace(eventStatus.JobId)) continue;
            if(merged.TryGetValue(eventStatus.JobId,out var existing))
            {
                // An older, delayed progress event must NEVER downgrade an
                // already confirmed terminal Job to "Running".
                if(Terminal(existing.State) && !Terminal(eventStatus.State)) continue;
                merged[eventStatus.JobId]=new WorkerJobStatus {
                    JobId=eventStatus.JobId,ToolName=eventStatus.ToolName,
                    State=eventStatus.State,Fraction=eventStatus.Fraction??existing.Fraction,
                    QueuePosition=eventStatus.QueuePosition,
                    Message=eventStatus.Message??existing.Message,
                    ElapsedSeconds=existing.ElapsedSeconds
                };
            }
            else merged[eventStatus.JobId]=eventStatus;
        }
        return merged.Values.ToArray();
    }

    internal Delta GetDelta(string owner, bool scoped, string? previousCursor,
        long generation, bool busy, bool statusAvailable, IReadOnlyList<WorkerJobStatus> reportedJobs,
        IReadOnlyList<object> ownerTasks, Func<string, bool> isOwned)
    {
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (scoped && string.IsNullOrWhiteSpace(owner))
            throw new InvalidOperationException("Scoped Job delta requires an authenticated owner.");
        var jobs = reportedJobs
            .Where(job => !scoped || isOwned(job.JobId))
            .Where(job => !string.IsNullOrWhiteSpace(job.JobId) && job.JobId.Length <= 128)
            .OrderBy(job => job.JobId, StringComparer.Ordinal)
            .Take(256)
            .Select(job => new JobView(job.JobId, job.ToolName, job.State,
                job.Fraction, job.QueuePosition, job.Message, job.ElapsedSeconds))
            .ToArray();
        // Shared/local diagnostics never expose someone else's official Task
        // IDs. Only scoped bearer credentials have owner-scoped Task metadata.
        var tasks = scoped ? ownerTasks.Take(25).ToArray() : Array.Empty<object>();
        // For scoped credentials, global WorkerBusy/StatusAvailable may change
        // solely because another owner is tracing. Do NOT fold those flags into
        // this owner's HMAC cursor. Use only owned Job observations instead.
        var visibleBusy = scoped
            ? jobs.Any(j => j.State.Equals("Queued",StringComparison.OrdinalIgnoreCase) ||
                            j.State.Equals("Running",StringComparison.OrdinalIgnoreCase) ||
                            j.State.Equals("Cancelling",StringComparison.OrdinalIgnoreCase) ||
                            j.State.Equals("Working",StringComparison.OrdinalIgnoreCase))
            : busy;
        var visibleStatus = scoped
            ? jobs.Any(j => !j.State.Equals("Unknown",StringComparison.OrdinalIgnoreCase))
            : statusAvailable;
        var snapshot = new Snapshot(generation, scoped, visibleBusy, visibleStatus, jobs, tasks);

        // A random process-local HMAC key prevents offline probing of a tiny
        // state space such as "queued"/"completed". Scoped cursors are unique
        // to an authenticated owner and are invalid after Host restart.
        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        var scope = (scoped ? "owner:" + owner : "shared") + "\n" + payload;
        var cursor = Convert.ToHexString(HMACSHA256.HashData(_secret,
            Encoding.UTF8.GetBytes(scope))).ToLowerInvariant();
        var changed = !string.Equals(previousCursor, cursor, StringComparison.Ordinal);
        return new Delta(cursor, changed, changed ? snapshot : null);
    }
}
