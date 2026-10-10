using System.ComponentModel;
using System.Diagnostics;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;
using ZOSAPI.Tools;
using ZOSAPI.Tools.RayTrace;

namespace ZemaxMCP.Server.Tools.NonSequential;

[ZemaxToolType]
public sealed class RunNscRayTraceTool
{
    private readonly IZemaxSession _session;
    private readonly McpJobManager _jobs;

    public RunNscRayTraceTool(IZemaxSession session, McpJobManager jobs)
    {
        _session = session;
        _jobs = jobs;
    }

    public record Result(
        bool Success,
        string? Error,
        string State,
        int DetectorObjects,
        bool DetectorsCleared,
        bool SplitRays,
        bool ScatterRays,
        bool UsePolarization,
        bool IgnoreErrors,
        double RuntimeSeconds,
        string? JobId = null,
        NscSameTraceLedger.Report? SameTraceEnergy = null);

    [ZemaxTool(Name = "zemax_run_nsc_ray_trace")]
    [Description("Run the official non-sequential ray trace against the current NSC system without creating a ZRD file. Detector buffers may be cleared/updated, so this is a Caution operation. The run is bounded, cancellable, and can execute as a managed background Job.")]
    public async Task<Result> ExecuteAsync(
        [Description("Clear detector buffers before tracing")] bool clearDetectors = true,
        [Description("Detector object to clear; 0 clears all detectors when clearDetectors=true")] int detectorObject = 0,
        [Description("Enable NSC ray splitting")] bool splitRays = false,
        [Description("Enable NSC scattering")] bool scatterRays = false,
        [Description("Trace polarization")] bool usePolarization = false,
        [Description("Ask OpticStudio to ignore recoverable ray-trace errors")] bool ignoreErrors = true,
        [Description("Wall-clock timeout in seconds (1-3600)")] double timeoutSeconds = 60,
        [Description("Queue the trace as a managed Job and return immediately")] bool runInBackground = true,
        [Description("Optional explicit 1..16 unique NSC scalar detector IDs. If present, REQUIRE clearDetectors=true and detectorObject=0 so the returned detector flux snapshot belongs to THIS successful trace, not previous traces. A background Job includes the ledger only in its finished result.")] int[]? snapshotDetectorObjects = null,
        [Description("Optional USER-declared launched native flux from precisely this trace/source set, strictly positive. Ratios are per detector only; optical coating/absorption/clipping losses cannot be reconstructed from a detector sum.")] double? declaredLaunchedFlux = null,
        [Description("Alongside a same-trace detector snapshot, also read up to 128 current NCE IObjectSources configured Power, analysis-ray count and wavelength number. Configuration power is not measured emitted flux and is NOT used as an automatic efficiency denominator.")] bool includeConfiguredSourcePower = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateInputs(detectorObject, timeoutSeconds);
            if (snapshotDetectorObjects is {Length:>0})
            {
                if (!clearDetectors || detectorObject!=0)
                    throw new ArgumentException("A same-trace detector snapshot requires clearDetectors=true and detectorObject=0 (clear ALL detectors).");
                if (snapshotDetectorObjects.Length>16 ||
                    snapshotDetectorObjects.Any(id=>id<1) ||
                    snapshotDetectorObjects.Distinct().Count()!=snapshotDetectorObjects.Length)
                    throw new ArgumentException("Provide 1..16 unique positive scalar detector IDs.");
                if (declaredLaunchedFlux.HasValue &&
                    (!double.IsFinite(declaredLaunchedFlux.Value) || declaredLaunchedFlux.Value<=0))
                    throw new ArgumentException("User-declared launched flux must be finite and positive.");
            }
            else if (declaredLaunchedFlux.HasValue || includeConfiguredSourcePower)
                throw new ArgumentException("Source metadata and declaredLaunchedFlux require snapshotDetectorObjects.");

            if (!runInBackground)
                return await ExecuteCoreAsync(
                    clearDetectors, detectorObject, splitRays, scatterRays,
                    usePolarization, ignoreErrors, timeoutSeconds,
                    snapshotDetectorObjects, declaredLaunchedFlux, includeConfiguredSourcePower,
                    cancellationToken).ConfigureAwait(false);

            var job = _jobs.Enqueue("zemax_run_nsc_ray_trace", async context =>
            {
                context.ReportProgress(0, "Waiting for the ZOS-API job slot.");
                var result = await ExecuteCoreAsync(
                    clearDetectors, detectorObject, splitRays, scatterRays,
                    usePolarization, ignoreErrors, timeoutSeconds,
                    snapshotDetectorObjects, declaredLaunchedFlux, includeConfiguredSourcePower,
                    context.CancellationToken).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException(result.Error ?? "NSC ray trace failed.");
                context.SetResult(result);
                context.ReportProgress(1, "NSC ray trace completed.");
            }, TimeSpan.FromSeconds(timeoutSeconds + 30));

            return new Result(
                true, null, "Queued", 0, false,
                splitRays, scatterRays, usePolarization, ignoreErrors, 0, job.JobId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(
                false, ex.Message, "Error", 0, false,
                splitRays, scatterRays, usePolarization, ignoreErrors, 0);
        }
    }

    private async Task<Result> ExecuteCoreAsync(
        bool clearDetectors,
        int detectorObject,
        bool splitRays,
        bool scatterRays,
        bool usePolarization,
        bool ignoreErrors,
        double timeoutSeconds,
        int[]? snapshotDetectorObjects,
        double? declaredLaunchedFlux,
        bool includeConfiguredSourcePower,
        CancellationToken cancellationToken)
    {
        return await _session.ExecuteAsync(
            "NscRayTrace",
            new Dictionary<string, object?>
            {
                ["clearDetectors"] = clearDetectors,
                ["detectorObject"] = detectorObject,
                ["splitRays"] = splitRays,
                ["scatterRays"] = scatterRays,
                ["usePolarization"] = usePolarization,
                ["ignoreErrors"] = ignoreErrors,
                ["timeoutSeconds"] = timeoutSeconds,
                ["snapshotDetectorObjects"] = snapshotDetectorObjects,
                ["declaredLaunchedFlux"] = declaredLaunchedFlux,
                ["includeConfiguredSourcePower"] = includeConfiguredSourcePower
            },
            system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (system.Mode != SystemType.NonSequential)
                    throw new InvalidOperationException("The current system is sequential. Open a non-sequential system before running an NSC ray trace.");

                var nce = system.NCE ?? throw new InvalidOperationException("Non-Sequential Component Editor is not available.");
                if (detectorObject > nce.NumberOfObjects)
                    throw new ArgumentOutOfRangeException(
                        nameof(detectorObject),
                        $"detectorObject {detectorObject} exceeds the {nce.NumberOfObjects} objects in the current NSC system.");
                if (detectorObject > 0)
                {
                    var detector = nce.GetObjectAt(detectorObject)
                        ?? throw new InvalidOperationException($"OpticStudio returned no NCE row for object {detectorObject}.");
                    if (!NscObjectClassification.IsDetector(detector))
                        throw new ArgumentException($"NSC object {detectorObject} ({detector.TypeName}) is not a detector.", nameof(detectorObject));
                }

                var detectorCount = 0;
                for (var number = 1; number <= nce.NumberOfObjects; number++)
                    if (nce.GetObjectAt(number) is { } row && NscObjectClassification.IsDetector(row)) detectorCount++;

                var trace = system.Tools?.OpenNSCRayTrace()
                    ?? throw new InvalidOperationException("OpticStudio did not open the NSC Ray Trace tool.");
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    trace.SplitNSCRays = splitRays;
                    trace.ScatterNSCRays = scatterRays;
                    trace.UsePolarization = usePolarization;
                    trace.IgnoreErrors = ignoreErrors;

                    // This MCP tool intentionally does not write ZRD files.
                    trace.SaveRays = false;

                    if (clearDetectors)
                        trace.ClearDetectors(detectorObject);

                    if (!trace.IsValid)
                        throw new InvalidOperationException("NSC Ray Trace settings are not valid for the current system.");

                    RunBounded(trace, timeoutSeconds, cancellationToken);
                    stopwatch.Stop();

                    if (!trace.Succeeded)
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(trace.ErrorMessage)
                                ? "NSC Ray Trace completed without success."
                                : trace.ErrorMessage);

                    NscSameTraceLedger.Report? ledger = null;
                    if(snapshotDetectorObjects is {Length:>0})
                    {
                        var readings=new List<(int Id,string Type,double Flux,double Hits)>();
                        foreach(var id in snapshotDetectorObjects)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if(id>nce.NumberOfObjects)
                                throw new ArgumentOutOfRangeException(nameof(snapshotDetectorObjects),
                                    "Detector ID is outside the active NSC object table.");
                            var detector=nce.GetObjectAt(id)
                                ?? throw new InvalidOperationException("Missing detector object "+id);
                            if(!NscObjectClassification.IsDetector(detector) ||
                               detector.Type is ZOSAPI.Editors.NCE.ObjectType.DetectorColor or
                                   ZOSAPI.Editors.NCE.ObjectType.DetectorPolar)
                                throw new ArgumentException("Only scalar NSC detectors are supported by same-trace flux snapshot: "+id);
                            if(!nce.GetDetectorData(id,0,0,out var flux) ||
                               !nce.GetDetectorData(id,-3,0,out var hits))
                                throw new InvalidOperationException("Native post-trace detector flux/hits unavailable: "+id);
                            readings.Add((id,detector.TypeName,flux,hits));
                        }
                        List<NscSameTraceLedger.ConfiguredSource>? configured=null;
                        if(includeConfiguredSourcePower)
                        {
                            configured=new List<NscSameTraceLedger.ConfiguredSource>();
                            for(var sourceId=1;sourceId<=nce.NumberOfObjects;sourceId++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var sourceRow=nce.GetObjectAt(sourceId);
                                if(sourceRow?.ObjectData is not ZOSAPI.Editors.NCE.IObjectSources source)continue;
                                if(configured.Count==128)
                                    throw new InvalidOperationException("More than 128 NSC source objects: configured source-power audit is bounded.");
                                configured.Add(new NscSameTraceLedger.ConfiguredSource(
                                    sourceId,source.Power,source.NumberOfAnalysisRays,source.WaveNumber));
                            }
                        }
                        ledger=NscSameTraceLedger.Build(declaredLaunchedFlux,readings,
                            clearedAllDetectors:clearDetectors && detectorObject==0,
                            configuredSources:configured);
                    }
                    return new Result(
                        true,
                        null,
                        "Completed",
                        detectorCount,
                        clearDetectors,
                        splitRays,
                        scatterRays,
                        usePolarization,
                        ignoreErrors,
                        stopwatch.Elapsed.TotalSeconds,
                        SameTraceEnergy:ledger);
                }
                finally
                {
                    StopIfStillRunning(trace);
                    trace.Close();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void RunBounded(INSCRayTrace trace, double timeoutSeconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!trace.Run())
            throw new InvalidOperationException("OpticStudio failed to start the NSC Ray Trace.");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelAndDrain(trace);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (stopwatch.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                CancelAndDrain(trace);
                throw new TimeoutException($"NSC Ray Trace exceeded the {timeoutSeconds:G} second wall-clock limit.");
            }

            var remaining = Math.Max(0.01, timeoutSeconds - stopwatch.Elapsed.TotalSeconds);
            var status = trace.WaitWithTimeout(Math.Min(0.25, remaining));
            switch (status)
            {
                case RunStatus.Completed:
                    return;
                case RunStatus.TimedOut:
                    continue;
                case RunStatus.FailedToStart:
                    throw new InvalidOperationException("NSC Ray Trace failed to start.");
                case RunStatus.InvalidTimeout:
                    throw new InvalidOperationException("OpticStudio rejected the NSC Ray Trace polling timeout.");
                default:
                    throw new InvalidOperationException($"Unexpected NSC Ray Trace run status: {status}.");
            }
        }
    }

    private static void CancelAndDrain(INSCRayTrace trace)
    {
        if (trace.IsRunning && trace.CanCancel && !trace.Cancel())
            throw new InvalidOperationException("OpticStudio rejected NSC Ray Trace cancellation.");
        if (trace.IsRunning && !trace.WaitForCompletion())
            throw new InvalidOperationException("NSC Ray Trace did not drain after cancellation.");
    }

    private static void StopIfStillRunning(INSCRayTrace trace)
    {
        if (!trace.IsRunning) return;
        try
        {
            if (trace.CanCancel) trace.Cancel();
            trace.WaitForCompletion();
        }
        catch { }
    }

    private static void ValidateInputs(int detectorObject, double timeoutSeconds)
    {
        if (detectorObject < 0)
            throw new ArgumentOutOfRangeException(nameof(detectorObject), "detectorObject must be 0 (all) or a positive detector object number.");
        if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) ||
            timeoutSeconds < 1 || timeoutSeconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "timeoutSeconds must be finite and between 1 and 3600.");
    }
}
