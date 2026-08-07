using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Services.GlassCatalog;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            VerifyOperationMetadataAndSnapshotBoundary();
            VerifyGlassCatalogSafety();
            await VerifyStaDispatcherAsync();
            await VerifyJobManagerAsync();
            Console.WriteLine("Core safety abstraction, glass-catalog integrity, STA dispatcher, and server job simulation tests passed.");
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
