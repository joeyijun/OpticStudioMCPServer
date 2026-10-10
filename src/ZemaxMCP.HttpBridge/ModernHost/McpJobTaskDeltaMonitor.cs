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

    internal sealed record Snapshot(long WorkerGeneration, bool WorkerBusy, bool StatusAvailable,
        IReadOnlyList<JobView> Jobs, IReadOnlyList<object> Tasks);

    internal sealed record Delta(string Cursor, bool Changed, Snapshot? Snapshot);

    internal Delta GetDelta(string owner, bool scoped, string? previousCursor,
        long generation, bool busy, IReadOnlyList<WorkerJobStatus> reportedJobs,
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
        var snapshot = new Snapshot(generation, busy, reportedJobs != null, jobs, tasks);

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
