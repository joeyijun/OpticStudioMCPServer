using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Services.GlassCatalog;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.Server.Tools.Base;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            VerifyOperationMetadataAndSnapshotBoundary();
            VerifyScientificNumberTruthfulness();
            VerifyGlassCatalogSafety();
            await VerifyStaDispatcherAsync();
            await VerifyJobManagerAsync();
            await VerifyJobLimitsAsync();
            await VerifyJobHardRecoveryAsync();
            Console.WriteLine("Core safety abstraction, scientific-number truthfulness, glass-catalog integrity, STA dispatcher, and bounded server job simulation tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyOperationMetadataAndSnapshotBoundary()
    {
        Assert(ZemaxOperationMetadata.GetCommandImpact("SetSurface") == ZemaxOperationImpact.HighImpact, "SetSurface must be high impact.");
        Assert(ZemaxOperationMetadata.GetCommandImpact("FutureUnclassifiedMutation") == ZemaxOperationImpact.HighImpact, "Unknown commands must fail closed.");
        Assert(ZemaxOperationMetadata.GetToolImpact("zemax_set_surface") == ZemaxOperationImpact.HighImpact, "Tool metadata must use the shared high-impact policy.");
        Assert(ZemaxOperationMetadata.GetToolImpact("future_tool") == ZemaxOperationImpact.Caution, "Unknown tools must not be displayed as read-only.");

        var root = Path.Combine(Path.GetTempPath(), "ZemaxMCP-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var oldReadOnly = Environment.GetEnvironmentVariable("ZEMAX_MCP_READ_ONLY");
        var oldSnapshots = Environment.GetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR");
        try
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", "false");
            Environment.SetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR", root);
            var safety = new ZemaxOperationSafety();
            var fake = new FakeSnapshotSystem("C:\\Designs\\demo.zos");
            safety.BeforeOperation(fake, "SetSurface");
            Assert(fake.CopyCalls == 1 && fake.LastCopy?.Closed == true, "High-impact safety must snapshot through the ZOS abstraction and close the copy.");
            Assert(File.Exists(safety.LastSnapshotPath!), "Safety snapshot was not written by the simulated system.");

            safety.BeforeOperation(fake, "GetSystem");
            Assert(fake.CopyCalls == 1, "Read-only operations must not create a snapshot.");

            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", "true");
            var readOnly = new ZemaxOperationSafety();
            AssertThrows<InvalidOperationException>(() => readOnly.BeforeOperation(fake, "SetSurface"), "Read-only mode did not block a high-impact operation.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ZEMAX_MCP_READ_ONLY", oldReadOnly);
            Environment.SetEnvironmentVariable("ZEMAX_MCP_SNAPSHOT_DIR", oldSnapshots);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void VerifyScientificNumberTruthfulness()
    {
        Assert(Math.Abs(1.25.Sanitize() - 1.25) < 1e-12, "Finite scientific values must be preserved exactly.");
        AssertThrows<InvalidDataException>(
            () => double.NaN.Sanitize(),
            "NaN must not be rewritten into a plausible finite measurement.");
        AssertThrows<InvalidDataException>(
            () => double.PositiveInfinity.Sanitize(),
            "Infinity must not be rewritten into an arbitrary finite measurement.");
        Assert(double.PositiveInfinity.SanitizeRadius() == 0,
            "Infinite optical radius should retain the established plane-surface convention.");
        AssertThrows<InvalidDataException>(
            () => double.NaN.SanitizeRadius(),
            "NaN radius must not be misreported as a plane surface.");
    }

    private static void VerifyGlassCatalogSafety()
    {
        AssertThrows<ArgumentException>(
            () => CatalogExportService.ValidateCatalogName(@"..\escape"),
            "Glass catalog names must not permit path traversal.");
        AssertThrows<ArgumentException>(
            () => CatalogExportService.ValidateCatalogName("CON"),
            "Glass catalog names must reject reserved Windows device names.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => GlassFilterService.Validate(new GlassFilterCriteria { Wn = -1 }),
            "Glass filters must reject negative distance weights.");
        AssertThrows<ArgumentOutOfRangeException>(
            () => GlassFilterService.Validate(new GlassFilterCriteria { DistanceRadius = double.NaN }),
            "Glass filters must reject non-finite values.");
        AssertThrows<ArgumentException>(
            () => GlassFilterService.Validate(new GlassFilterCriteria { NdMin = 1.7, NdMax = 1.6 }),
            "Glass filters must reject contradictory min/max bounds.");

        var root = Path.Combine(Path.GetTempPath(), "ZemaxMCP-glass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var safePath = CatalogExportService.GetCatalogPath(root, "SAFE");
            Assert(Path.GetDirectoryName(safePath) == root, "Safe catalog path did not remain in the requested Glasscat directory.");

            var glass = new GlassEntry
            {
                Name = "TEST",
                CatalogName = "SOURCE",
                Nd = 1.5168,
                Vd = 64.17,
                RawLines = new List<string>
                {
                    "NM TEST 2 0 1.5168 64.17 0 1",
                    "LD 0.4 0.7"
                }
            };

            File.WriteAllText(safePath, "original");
            AssertThrows<IOException>(
                () => CatalogExportService.Export(new[] { glass }, safePath, "SAFE", overwrite: false),
                "overwrite=false must remain a final no-clobber guarantee.");
            Assert(File.ReadAllText(safePath) == "original", "A rejected no-overwrite export modified the existing catalog.");

            CatalogExportService.Export(new[] { glass }, safePath, "SAFE", overwrite: true);
            var exported = File.ReadAllText(safePath);
            Assert(exported.Contains("NM TEST 2 0 1.5168 64.17 0 1", StringComparison.Ordinal), "Overwrite export did not publish the expected AGF contents.");

            var validAgf = Path.Combine(root, "VALID.agf");
            File.WriteAllLines(validAgf, new[]
            {
                "NM VALID 2 0 1.5168 64.17 0 1",
                "LD 0.4 0.7"
            });
            var parsed = AgfFileParser.ParseCatalog(validAgf, "VALID");
            Assert(parsed.Count == 1 && parsed[0].Name == "VALID" && Math.Abs(parsed[0].Nd - 1.5168) < 1e-12,
                "Valid AGF data was not parsed as expected.");

            var malformedAgf = Path.Combine(root, "BAD.agf");
            File.WriteAllText(malformedAgf, "NM BAD 2 0 1.5168 not-a-vd 0 1");
            try
            {
                AgfFileParser.ParseCatalog(malformedAgf, "BAD");
                throw new InvalidOperationException("Malformed AGF numeric data was accepted.");
            }
            catch (FormatException exception)
            {
                Assert(exception.Message.Contains("line 1", StringComparison.OrdinalIgnoreCase), "Malformed AGF error did not identify the source line.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task VerifyStaDispatcherAsync()
    {
        using var dispatcher = new ZosApiDispatcher();
        var threadIds = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => dispatcher.GetExecutingThreadIdAsync()));
        Assert(threadIds.Distinct().Count() == 1 && threadIds[0] == dispatcher.ThreadId, "ZOS dispatcher did not serialize calls onto one long-lived thread.");
        Assert(dispatcher.ApartmentState == ApartmentState.STA, "ZOS dispatcher must run in STA.");
    }

    private static async Task VerifyJobManagerAsync()
    {
        using var jobs = new McpJobManager();
        var completed = new TaskCompletionSource<McpJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        jobs.JobChanged += snapshot =>
        {
            if (snapshot.ToolName == "simulated-long-operation" && snapshot.State == McpJobState.Cancelled)
                completed.TrySetResult(snapshot);
        };
        var queued = jobs.Enqueue("simulated-long-operation", async context =>
        {
            context.ReportProgress(0.25, "started");
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
        }, TimeSpan.FromSeconds(5));
        Assert(jobs.Cancel(queued.JobId, out _), "Queued/running job could not be cancelled.");
        var terminal = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(terminal.State == McpJobState.Cancelled, "Cancelled job did not reach a terminal cancelled state.");
    }

    private static async Task VerifyJobLimitsAsync()
    {
        using (var boundedQueue = new McpJobManager(maxHistory: 4, maxPending: 2))
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            boundedQueue.Enqueue("blocking", async context =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            boundedQueue.Enqueue("queued-1", _ => Task.CompletedTask);
            boundedQueue.Enqueue("queued-2", _ => Task.CompletedTask);
            AssertThrows<InvalidOperationException>(
                () => boundedQueue.Enqueue("queued-overflow", _ => Task.CompletedTask),
                "Background jobs beyond the configured pending limit must be rejected.");
            release.TrySetResult();
        }

        using (var boundedHistory = new McpJobManager(maxHistory: 3, maxPending: 8))
        {
            var completedCount = 0;
            var allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            boundedHistory.JobChanged += snapshot =>
            {
                if (snapshot.State == McpJobState.Completed &&
                    snapshot.ToolName.StartsWith("history-", StringComparison.Ordinal) &&
                    Interlocked.Increment(ref completedCount) == 5)
                    allCompleted.TrySetResult();
            };

            for (var index = 0; index < 5; index++)
                boundedHistory.Enqueue("history-" + index, _ => Task.CompletedTask);

            await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            var retained = boundedHistory.List();
            Assert(retained.Count == 3, "Completed job history must be trimmed to the configured retention limit.");
            Assert(retained.All(job => job.State == McpJobState.Completed), "Retained job history unexpectedly contains non-terminal jobs.");
        }
    }

    private static async Task VerifyJobHardRecoveryAsync()
    {
        var recovery = new TaskCompletionSource<McpJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var jobs = new McpJobManager(
            maxHistory: 4,
            maxPending: 2,
            cancellationGrace: TimeSpan.FromMilliseconds(75),
            hardRecoveryAction: snapshot => recovery.TrySetResult(snapshot));

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = jobs.Enqueue("hung-background-job", async _ =>
        {
            started.TrySetResult();
            await never.Task;
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(jobs.Cancel(job.JobId, out _), "A running background job could not enter cancellation.");
        var hardFailure = await recovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(hardFailure.JobId == job.JobId && hardFailure.State == McpJobState.Failed,
            "A non-draining background job did not transition to failed hard-recovery state.");
        Assert(hardFailure.Message.Contains("hard recovery", StringComparison.OrdinalIgnoreCase),
            "Hard-recovery failure did not explain why the Worker generation must be replaced.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class FakeSnapshotSystem : IZosSystemSnapshot
    {
        public FakeSnapshotSystem(string systemFile) => SystemFile = systemFile;
        public string? SystemFile { get; }
        public int CopyCalls { get; private set; }
        public FakeSnapshotSystem? LastCopy { get; private set; }
        public bool Closed { get; private set; }
        public IZosSystemSnapshot? CopySystem()
        {
            CopyCalls++;
            LastCopy = new FakeSnapshotSystem(SystemFile!);
            return LastCopy;
        }
        public void SaveAs(string path) => File.WriteAllText(path, "simulated-zos-snapshot");
        public void Close(bool saveChanges) => Closed = true;
    }
}
