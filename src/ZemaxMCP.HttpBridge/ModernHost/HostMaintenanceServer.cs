using System.IO.Pipes;
using System.Text;

namespace ZemaxMCP.HttpBridge.ModernHost;

// Local administrative boundary only. Same Windows user; never exposed over HTTP/LAN.
// Admission stays frozen after acknowledgement, so a subsequent tool cannot race an update.
internal sealed class HostMaintenanceServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _pump;
    internal HostMaintenanceServer(HostRequestAdmission admission, WorkerRpcClient worker, OpticStudioControlLease lease)
    {
        _pump = Task.Run(async () =>
        {
            string? ownerTicket = null;
            while (!_shutdown.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream("ZemaxMcpMaintenance-" + Environment.ProcessId,
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var preparedHere = false;
                try
                {
                    await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                    var command = await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false);
                    var parts = command?.Split(':');
                    var valid = parts is { Length: 2 } && Guid.TryParseExact(parts[1], "N", out _);
                    string response;
                    if (valid && parts![0] == "resume" && ownerTicket == parts[1])
                    {
                        worker.ResumeAfterMaintenance();
                        admission.Resume();
                        ownerTicket = null;
                        response = "resumed";
                    }
                    else if (!valid || parts![0] != "prepare") response = "busy:Unknown command or maintenance owner.";
                    else if (ownerTicket != null && ownerTicket != parts[1]) response = "busy:Another local update owns maintenance.";
                    else if (!admission.TryFreezeIfIdle()) response = "busy:An optical request is active or queued.";
                    else
                    {
                        try
                        {
                            if (lease.HasActiveBackgroundJobs) throw new InvalidOperationException("An optical background Job is active.");
                            var pid = await worker.PrepareShutdownAsync(deadline.Token).ConfigureAwait(false);
                            preparedHere = true;
                            ownerTicket = parts[1];
                            response = "ready:" + (pid ?? 0);
                        }
                        catch (Exception ex)
                        {
                            worker.ResumeAfterMaintenance();
                            admission.Resume();
                            ownerTicket = null;
                            response = "busy:" + ex.Message.Replace('\n', ' ').Replace('\r', ' ');
                        }
                    }
                    await writer.WriteLineAsync(response.AsMemory(), deadline.Token).ConfigureAwait(false);
                    preparedHere = false; // caller now owns resume/update handoff
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    if (preparedHere) { worker.ResumeAfterMaintenance(); admission.Resume(); ownerTicket = null; }
                    Serilog.Log.Warning(ex, "Local maintenance handshake failed");
                }
            }
        });
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _pump.ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
