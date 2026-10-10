using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Services.GlassCatalog;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.Server.Tools.Analysis;
using ZemaxMCP.Server.Tools.Base;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            VerifyOperationMetadataAndSnapshotBoundary();
            VerifyScientificNumberTruthfulness();
            VerifyStructuredMtf();
            VerifySequentialEnergyWindows();
            VerifyGlassCatalogSafety();
            await VerifyStaDispatcherAsync();
            await VerifyJobManagerAsync();
            await VerifyJobLimitsAsync();
            await VerifyJobHardRecoveryAsync();
            Console.WriteLine("Core safety abstraction, scientific-number truthfulness, bounded LDE energy windows, glass-catalog integrity, STA dispatcher, and bounded server job simulation tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifySequentialEnergyWindows()
    {
        Assert(SequentialEnergySurfaceRange.Resolve(1, 24, 80) == (1, 24, 24),
            "First bounded LDE window was not 1..24.");
        Assert(SequentialEnergySurfaceRange.Resolve(24, 47, 80) == (24, 47, 24),
            "Adjacent windows must use absolute indices with one-surface overlap.");
        Assert(SequentialEnergySurfaceRange.Resolve(57, 0, 80) == (57, 80, 24),
            "FinalSurface=0 must mean the image plane.");
        Assert(SequentialEnergySurfaceRange.Resolve(80, 80, 80) == (80, 80, 1),
            "One-surface tail window must remain valid.");
        AssertThrows<ArgumentException>(() => SequentialEnergySurfaceRange.Resolve(1, 0, 80),
            "Oversized implicit whole-system window was accepted.");
        AssertThrows<ArgumentException>(() => SequentialEnergySurfaceRange.Resolve(24, 22, 80),
            "Reversed range was accepted.");
        AssertThrows<ArgumentOutOfRangeException>(() => SequentialEnergySurfaceRange.Resolve(0, 3, 80),
            "Surface zero was accepted as start.");
        AssertThrows<ArgumentOutOfRangeException>(() => SequentialEnergySurfaceRange.Resolve(10, 81, 80),
            "A final surface outside the LDE was accepted.");
        AssertThrows<ArgumentOutOfRangeException>(() => SequentialEnergySurfaceRange.Resolve(2, -1, 80),
            "A negative final surface was accepted.");
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
        foreach (var radius in new[] { 0.0, double.PositiveInfinity, double.NegativeInfinity })
        {
            var rawRadius = radius;
            var readback = SurfaceReadback.FromRaw(2, rawRadius, double.PositiveInfinity,
                "", double.NegativeInfinity, 0, "plane", false);
            using var parsed = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(readback));
            Assert(parsed.RootElement.GetProperty("Radius").GetDouble() == 0 &&
                parsed.RootElement.GetProperty("Thickness").ValueKind == System.Text.Json.JsonValueKind.Null &&
                parsed.RootElement.GetProperty("ThicknessState").GetString() == "PositiveInfinity" &&
                parsed.RootElement.GetProperty("SemiDiameterState").GetString() == "NegativeInfinity",
                "Batch surface readback must serialize planes and signed optical infinity safely.");
            Assert(rawRadius.Equals(radius), "Wire normalization must not modify the raw rollback value.");
        }
        var finiteSurface = SurfaceReadback.FromRaw(1, 12.5, 1.25, "N-BK7", 2, -1, "finite", true);
        Assert(finiteSurface.Radius == 12.5 && finiteSurface.Thickness == 1.25 && finiteSurface.SemiDiameter == 2 &&
            finiteSurface.Conic == -1 && finiteSurface.IsStop && finiteSurface.ThicknessState == "Finite",
            "Batch readback must preserve finite values and surface metadata.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, double.NaN, 1, "", 1, 0, "", false), "NaN radius accepted.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, 0, double.NaN, "", 1, 0, "", false), "NaN thickness accepted.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, 0, 1, "", double.NaN, 0, "", false), "NaN semi-diameter accepted.");
        AssertThrows<InvalidDataException>(() => SurfaceReadback.FromRaw(1, 0, 1, "", 1, double.PositiveInfinity, "", false), "Invalid conic accepted.");
        Assert(double.PositiveInfinity.OpticalDimension() == null &&
               double.PositiveInfinity.OpticalDimensionState() == "PositiveInfinity",
               "Optical infinity must be explicitly represented, not fabricated as a finite number.");
        Assert(double.NegativeInfinity.OpticalDimension() == null &&
               double.NegativeInfinity.OpticalDimensionState() == "NegativeInfinity", "Infinity sign was lost.");
        Assert(1.25.OpticalDimension() == 1.25 && 1.25.OpticalDimensionState() == "Finite", "Finite dimension changed.");
        AssertThrows<InvalidDataException>(() => double.NaN.OpticalDimension(), "NaN dimensions must remain errors.");
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
        var cardinal = new CardinalPoints { Success = true, Magnification = 0, Wavelength = 1 };
        var dimensions = typeof(CardinalPoints).GetProperties()
            .Where(property => property.PropertyType == typeof(double?)).ToArray();
        Assert(dimensions.Length == 9, "All nine cardinal optical dimensions must support explicit infinity.");
        foreach (var value in new[] { 1.25, double.PositiveInfinity, double.NegativeInfinity })
        {
            foreach (var dimension in dimensions)
            {
                dimension.SetValue(cardinal, value.OpticalDimension());
                cardinal.DimensionStates[dimension.Name] = value.OpticalDimensionState();
            }
            var json = System.Text.Json.JsonSerializer.Serialize(cardinal);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            foreach (var dimension in dimensions)
            {
                var field = document.RootElement.GetProperty(dimension.Name);
                Assert(double.IsInfinity(value) ? field.ValueKind == System.Text.Json.JsonValueKind.Null : field.GetDouble() == value,
                    "Cardinal-point infinity or finite readback was lost during strict JSON serialization.");
                Assert(document.RootElement.GetProperty("DimensionStates").GetProperty(dimension.Name).GetString() == value.OpticalDimensionState(),
                    "Cardinal-point JSON lost the infinity sign.");
            }
        }
    }

    private static void VerifyStructuredMtf()
    {
        var field = ZemaxMCP.Server.Tools.Analysis.MtfSeriesReader.Read("视场：0 度", 1,
            new[] { 0.0, 10.0 }, new double[,] { { 1, 1 }, { 0.8, 0.7 } });
        Assert(field.FieldLabel == "视场：0 度" && field.TangentialMtf![1] == 0.8 && field.SagittalMtf![1] == 0.7,
            "Structured MTF must preserve localized labels and distinguish tangential/sagittal columns.");
        AssertThrows<InvalidDataException>(() => ZemaxMCP.Server.Tools.Analysis.MtfSeriesReader.Read("bad", 1,
            new[] { 0.0 }, new double[,] { { double.NaN, 1 } }), "Invalid MTF measurements must fail.");
        AssertThrows<InvalidDataException>(() => ZemaxMCP.Server.Tools.Analysis.MtfSeriesReader.Read("bad", 1,
            new[] { 0.0, 10.0 }, new double[,] { { 1, 1 } }), "MTF shape mismatch must fail.");
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

        using var bounded = new ZosApiDispatcher(maxPending: 2);
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var blocking = bounded.InvokeAsync(() =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(8));
            return 1;
        });
        Assert(entered.Wait(TimeSpan.FromSeconds(3)), "STA operation did not start.");
        using var cancel = new CancellationTokenSource();
        var cancelled = bounded.InvokeAsync(() => 2, cancel.Token);
        var later = bounded.InvokeAsync(() => 3);
        Assert(bounded.PendingCount == 2, "STA queue did not track the bounded pending count.");
        AssertThrows<InvalidOperationException>(() => bounded.InvokeAsync(() => 4),
            "A full STA queue must fail closed instead of growing without bound.");
        cancel.Cancel();
        try
        {
            await cancelled.WaitAsync(TimeSpan.FromSeconds(2));
            throw new InvalidOperationException("Cancelled queued STA work unexpectedly executed.");
        }
        catch (OperationCanceledException) { }
        Assert(bounded.PendingCount == 1,
            "Cancellation should free a pending STA queue slot before a blocking COM operation returns.");
        var replacement = bounded.InvokeAsync(() => 5);
        Assert(bounded.PendingCount == 2, "STA queue slot was not reusable after cancellation.");
        release.Set();
        Assert(await blocking == 1 && await later == 3 && await replacement == 5,
            "Surviving STA work must execute in FIFO order after cancelling a queued item.");

        using var closing = new ZosApiDispatcher(maxPending: 1);
        using var closeEntered = new ManualResetEventSlim(false);
        using var closeRelease = new ManualResetEventSlim(false);
        var active = closing.InvokeAsync(() =>
        {
            closeEntered.Set();
            closeRelease.Wait(TimeSpan.FromSeconds(8));
            return 7;
        });
        Assert(closeEntered.Wait(TimeSpan.FromSeconds(3)), "Disposal test STA did not start.");
        var abandoned = closing.InvokeAsync(() => 8);
        closing.Dispose();
        try
        {
            await abandoned;
            throw new InvalidOperationException("Disposed STA accepted a queued operation.");
        }
        catch (ObjectDisposedException) { }
        closeRelease.Set();
        Assert(await active == 7, "An in-flight STA call must not access disposed queue state.");
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
        Assert(terminal.Elapsed == terminal.CompletedAt - terminal.StartedAt, "Cancelled elapsed time must end at cancellation.");
        await Task.Delay(70);
        Assert(jobs.Get(terminal.JobId)!.Elapsed == terminal.Elapsed, "Cancelled elapsed time must not grow on later polling.");
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

        using (var boundedHistory = new McpJobManager(maxHistory: 3, maxPending: 8, maxResultHistory: 2))
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
            {
                var resultValue = index;
                boundedHistory.Enqueue("history-" + index, context =>
                {
                    context.SetResult(new string('x', 1024) + resultValue);
                    return Task.CompletedTask;
                });
            }

            await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            var retained = boundedHistory.List();
            Assert(retained.Count == 3, "Completed job history must be trimmed to the configured retention limit.");
            Assert(retained.All(job => job.State == McpJobState.Completed), "Retained job history unexpectedly contains non-terminal jobs.");
            foreach (var job in retained)
                Assert(job.Elapsed == job.CompletedAt - job.StartedAt, "Completed job elapsed time must stop at completion.");
            var finished = retained[0];
            await Task.Delay(70);
            Assert(boundedHistory.Get(finished.JobId)!.Elapsed == finished.Elapsed, "Terminal elapsed time must not grow on later polling.");
            Assert(retained.Count(job => !job.ResultExpired && job.Result != null) == 2,
                "Only the configured newest result payloads should remain resident.");
            Assert(retained.Count(job => job.ResultExpired && job.Result == null) == 1,
                "Older retained job metadata must explicitly mark its discarded result payload.");
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
        var job = jobs.Enqueue("hung-background-job", async context =>
        {
            started.TrySetResult();
            await never.Task;
            // Simulate an abandoned, non-cooperative COM operation returning
            // after the hard-recovery decision was already published.
            context.SetResult("late-result-must-not-leak");
            context.ReportProgress(1, "late success");
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = jobs.Enqueue("must-not-run-after-hard-recovery", _ =>
        {
            queuedExecuted.TrySetResult();
            return Task.CompletedTask;
        });
        Assert(jobs.Cancel(job.JobId, out _), "A running background job could not enter cancellation.");
        var hardFailure = await recovery.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(hardFailure.JobId == job.JobId && hardFailure.State == McpJobState.Failed,
            "A non-draining background job did not transition to failed hard-recovery state.");
        Assert(hardFailure.Message.Contains("hard recovery", StringComparison.OrdinalIgnoreCase),
            "Hard-recovery failure did not explain why the Worker generation must be replaced.");
        var abandoned = jobs.Get(waiting.JobId);
        Assert(abandoned is { State: McpJobState.Failed } && !queuedExecuted.Task.IsCompleted,
            "A queued background job was not failed when its Worker generation required hard recovery.");
        AssertThrows<InvalidOperationException>(
            () => jobs.Enqueue("unsafe-post-recovery", _ => Task.CompletedTask),
            "A Worker with an orphaned COM call must reject all new background jobs.");
        never.TrySetResult();
        await Task.Delay(100);
        var stillFailed = jobs.Get(job.JobId);
        Assert(stillFailed is { State: McpJobState.Failed, Result: null } &&
               stillFailed.Message.Contains("hard recovery", StringComparison.OrdinalIgnoreCase),
            "A late COM result/progress resurrected an already-failed Job after hard recovery.");
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
