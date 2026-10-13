using System.Text.Json;
using ZemaxMCP.Core.Session;
using ZemaxMCP.HttpBridge.ModernHost;

namespace ZemaxMCP.PrivateRpcTests;

internal static partial class Program
{
    private static async Task VerifyReliabilityReviewFixesAsync()
    {
        var original = Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY");
        try
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", "true");
            var safety = new ZemaxOperationSafety();
            foreach (var command in new[] { "GetVignetting", "GetEnvironment", "GetPolarization", "GetStopSurface" })
            {
                if (!ZemaxOperationMetadata.IsKnownCommand(command) || ZemaxOperationSafety.RequiresSnapshot(command))
                    throw new Exception("Read command has incorrect safety metadata: " + command);
                // null snapshot boundary proves there is no snapshot/COM call.
                safety.BeforeOperation(null!, command);
            }
            foreach (var command in new[] { "SetVignetting", "ClearVignetting", "SetEnvironment", "SetPolarization", "SetStopSurface", "FutureUnknownCommand" })
            {
                if (!ZemaxOperationSafety.RequiresSnapshot(command)) throw new Exception("Mutation was not protected: " + command);
                try { safety.BeforeOperation(null!, command); }
                catch (InvalidOperationException) { continue; }
                throw new Exception("Read-only mode admitted a mutation: " + command);
            }
            if (safety.LastSnapshotPath != null) throw new Exception("Reads created a snapshot.");
        }
        finally { Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", original); }

        using var queue = new HostRequestAdmission(1, TimeSpan.FromMilliseconds(100));
        using (var active = await queue.AcquireAsync(CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource();
            var waiting = queue.AcquireAsync(cancellation.Token);
            try { using var unexpected = await queue.AcquireAsync(CancellationToken.None); }
            catch (RequestAdmissionException ex) when (ex.Code == "queue_full") { goto CancelWait; }
            throw new Exception("Queue capacity was not enforced.");
        CancelWait:
            cancellation.Cancel();
            try { using var unexpected = await waiting; }
            catch (OperationCanceledException) { goto Deadline; }
            throw new Exception("Queued cancellation did not cancel.");
        Deadline:
            try { using var unexpected = await queue.AcquireAsync(CancellationToken.None); }
            catch (RequestAdmissionException ex) when (ex.Code == "queue_timeout") { goto VerifyCounts; }
            throw new Exception("Queue wait deadline was not enforced.");
        VerifyCounts:
            using var data = JsonDocument.Parse(JsonSerializer.Serialize(queue.GetHealth()));
            if (data.RootElement.GetProperty("queued").GetInt32() != 0 || data.RootElement.GetProperty("active").GetInt32() != 1)
                throw new Exception("Queue slots leaked after cancellation/timeout.");
        }
        using (var next = await queue.AcquireAsync(CancellationToken.None)) { }
        foreach (var args in new[] { new[] { "--max-pending-requests", "0" }, new[] { "--queue-wait-timeout-seconds", "0" } })
        {
            try { HostOptions.Parse(args); }
            catch (ArgumentException) { continue; }
            throw new Exception("Invalid admission options accepted.");
        }
        Console.WriteLine("Read-only risk, mutation guards, bounded admission, cancellation, timeout and slot recovery passed.");
    }
}
