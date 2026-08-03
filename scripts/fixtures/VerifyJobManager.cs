using System;
using System.Threading;
using System.Threading.Tasks;
using ZemaxMCP.Server.Services.Jobs;

public static class VerifyJobManager
{
    public static int Main()
    {
        using (var jobs = new McpJobManager())
        using (var started = new ManualResetEventSlim())
        {
            var first = jobs.Enqueue("first", async context =>
            {
                started.Set();
                context.ReportProgress(0.5, "Halfway.");
                await Task.Delay(150, context.CancellationToken);
                context.SetResult("first-result");
            });
            if (!started.Wait(1000)) throw new Exception("First job did not start.");

            var second = jobs.Enqueue("second", async context =>
            {
                await Task.Delay(10, context.CancellationToken);
            });
            var queued = jobs.Get(second.get_JobId());
            if (queued == null || queued.get_State() != McpJobState.Queued || queued.get_QueuePosition() != 1)
                throw new Exception("Second job was not queued behind the running job.");
            McpJobSnapshot ignored;
            if (!jobs.Cancel(second.get_JobId(), out ignored)) throw new Exception("Queued job cancellation was rejected.");

            if (!SpinWait.SpinUntil(() => { var value = jobs.Get(first.get_JobId()); return value != null && value.get_State() == McpJobState.Completed; }, 3000))
                throw new Exception("First job did not complete.");
            var completed = jobs.Get(first.get_JobId());
            if (completed == null || completed.get_Progress() != 0.5 || (string)completed.get_Result() != "first-result")
                throw new Exception("Completed job did not retain progress and result.");
            if (!SpinWait.SpinUntil(() => { var value = jobs.Get(second.get_JobId()); return value != null && value.get_State() == McpJobState.Cancelled; }, 1000))
                throw new Exception("Queued cancellation did not reach the cancelled terminal state.");
        }
        return 0;
    }
}
