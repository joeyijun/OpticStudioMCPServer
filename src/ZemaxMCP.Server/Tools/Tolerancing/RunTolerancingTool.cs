using System.ComponentModel;
using System.Diagnostics;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Services.Jobs;
using ZemaxMCP.Server.Tooling;
using ZOSAPI;
using ZOSAPI.Tools;
using ZOSAPI.Tools.Tolerancing;

namespace ZemaxMCP.Server.Tools.Tolerancing;

[ZemaxToolType]
public sealed class RunTolerancingTool
{
    private const int MaximumMonteCarloRuns = 1000;
    private const int MaximumReturnedSensitivityOperands = 50;
    private readonly IZemaxSession _session;
    private readonly McpJobManager _jobs;

    public RunTolerancingTool(IZemaxSession session, McpJobManager jobs)
    {
        _session = session;
        _jobs = jobs;
    }

    public record ColumnStatistics(
        int ColumnIndex,
        string Name,
        int SampleSize,
        double? Minimum,
        double? Maximum,
        double? Mean,
        double? SampleStandardDeviation);

    public record SensitivityOperand(
        int OperandIndex,
        string OperandType,
        string Comment,
        double? Minimum,
        double? Maximum,
        double? EstimatedChangeMinimum,
        double? EstimatedChangeMaximum,
        double? WorstAbsoluteEstimatedChange);

    public record ThresholdEvaluation(
        string Column,
        double Threshold,
        string Direction,
        int FiniteSamples,
        int NonFiniteSamples,
        int PassingSamples,
        double? YieldFraction);

    public record Result(
        bool Success,
        string? Error,
        string State,
        string SetupMode,
        string Criterion,
        string CriterionComp,
        string CriterionField,
        string MonteCarloStatistic,
        int RequestedMonteCarloRuns,
        double RuntimeSeconds,
        string? Summary,
        bool SummaryTruncated,
        int MonteCarloRows,
        int MonteCarloColumns,
        IReadOnlyList<ColumnStatistics> MonteCarloStatistics,
        int SensitivityCriteria,
        int SensitivityCompensators,
        int SensitivityOperands,
        IReadOnlyList<SensitivityOperand> WorstSensitivityOperands,
        ThresholdEvaluation? Threshold,
        string? JobId = null);

    [ZemaxTool(Name = "zemax_run_tolerancing")]
    [Description("Run sequential OpticStudio tolerancing with bounded sensitivity/Monte Carlo settings and return structured ZTD results. No Monte Carlo lens files are saved. Optional yield evaluation requires an explicit threshold direction so the tool never guesses whether larger or smaller criterion values are preferable.")]
    public async Task<Result> ExecuteAsync(
        [Description("Run sensitivity before Monte Carlo. false selects SkipSensitivity.")] bool includeSensitivity = true,
        [Description("Criterion: RMSSpotRadius, RMSSpotX, RMSSpotY, RMSWavefront, MeritFunction, GeometricMTFAverage, GeometricMTFTan, GeometricMTFSag, DiffMTFAverage, DiffMTFTan, DiffMTFSag, BoresightError, RMSAngularRadius, RMSAngularX, RMSAngularY")] string criterion = "RMSSpotRadius",
        [Description("Criterion sampling value passed to OpticStudio; default 3 follows the official API example")] int criterionSampling = 3,
        [Description("Compensation: OptimizeAll_DLS, ParaxialFocus, None, OptimizeAll_OD")] string criterionComp = "OptimizeAll_DLS",
        [Description("Compensation cycle value passed to OpticStudio; default 2 follows the official API example")] int criterionCycle = 2,
        [Description("Field mode: Y_Symmetric, XY_Symmetric, UserDefined")] string criterionField = "UserDefined",
        [Description("Number of Monte Carlo runs (0-1000)")] int monteCarloRuns = 20,
        [Description("Monte Carlo statistics: Normal, Uniform, Parabolic")] string monteCarloStatistic = "Normal",
        [Description("Optional pass/fail threshold for the selected criterion column")] double? passThreshold = null,
        [Description("Required when passThreshold is set: LessOrEqual or GreaterOrEqual")] string? thresholdDirection = null,
        [Description("Maximum sensitivity operands returned after worst-effect sorting (1-50)")] int maxSensitivityOperands = 25,
        [Description("Wall-clock timeout in seconds (1-7200)")] double timeoutSeconds = 300,
        [Description("Queue the tolerancing run as a managed Job and return immediately")] bool runInBackground = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var parsedCriterion = ParseNamedEnum<Criterions>(criterion, nameof(criterion), SequentialCriteria);
            var parsedComp = ParseNamedEnum<CriterionComps>(criterionComp, nameof(criterionComp), SequentialComps);
            var parsedField = ParseNamedEnum<CriterionFields>(criterionField, nameof(criterionField), SequentialFields);
            var parsedStatistic = ParseNamedEnum<MonteCarloStatistics>(monteCarloStatistic, nameof(monteCarloStatistic), MonteCarloStatisticsNames);
            var direction = ValidateInputs(
                criterionSampling, criterionCycle, monteCarloRuns, passThreshold,
                thresholdDirection, maxSensitivityOperands, timeoutSeconds);

            if (!runInBackground)
                return await ExecuteCoreAsync(
                    includeSensitivity, parsedCriterion, criterionSampling, parsedComp, criterionCycle,
                    parsedField, monteCarloRuns, parsedStatistic, passThreshold, direction,
                    maxSensitivityOperands, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            var job = _jobs.Enqueue("zemax_run_tolerancing", async context =>
            {
                context.ReportProgress(0, "Waiting for the ZOS-API job slot.");
                var result = await ExecuteCoreAsync(
                    includeSensitivity, parsedCriterion, criterionSampling, parsedComp, criterionCycle,
                    parsedField, monteCarloRuns, parsedStatistic, passThreshold, direction,
                    maxSensitivityOperands, timeoutSeconds, context.CancellationToken).ConfigureAwait(false);
                if (!result.Success)
                    throw new InvalidOperationException(result.Error ?? "Tolerancing failed.");
                context.SetResult(result);
                context.ReportProgress(1, "Tolerancing completed.");
            }, TimeSpan.FromSeconds(timeoutSeconds + 60));

            return EmptyResult(
                true, null, "Queued", includeSensitivity,
                CanonicalCriterionName(parsedCriterion),
                CanonicalCompName(parsedComp),
                CanonicalFieldName(parsedField),
                parsedStatistic.ToString(),
                monteCarloRuns, job.JobId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmptyResult(
                false, ex.Message, "Error", includeSensitivity,
                criterion, criterionComp, criterionField, monteCarloStatistic, monteCarloRuns, null);
        }
    }

    private async Task<Result> ExecuteCoreAsync(
        bool includeSensitivity,
        Criterions criterion,
        int criterionSampling,
        CriterionComps criterionComp,
        int criterionCycle,
        CriterionFields criterionField,
        int monteCarloRuns,
        MonteCarloStatistics monteCarloStatistic,
        double? passThreshold,
        string? thresholdDirection,
        int maxSensitivityOperands,
        double timeoutSeconds,
        CancellationToken cancellationToken)
    {
        return await _session.ExecuteAsync(
            "RunTolerancing",
            new Dictionary<string, object?>
            {
                ["includeSensitivity"] = includeSensitivity,
                ["criterion"] = CanonicalCriterionName(criterion),
                ["criterionSampling"] = criterionSampling,
                ["criterionComp"] = CanonicalCompName(criterionComp),
                ["criterionCycle"] = criterionCycle,
                ["criterionField"] = CanonicalFieldName(criterionField),
                ["monteCarloRuns"] = monteCarloRuns,
                ["monteCarloStatistic"] = monteCarloStatistic.ToString(),
                ["passThreshold"] = passThreshold,
                ["thresholdDirection"] = thresholdDirection,
                ["maxSensitivityOperands"] = maxSensitivityOperands,
                ["timeoutSeconds"] = timeoutSeconds
            },
            system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (system.Mode == SystemType.NonSequential)
                    throw new InvalidOperationException("zemax_run_tolerancing currently supports sequential systems only.");

                var tde = system.TDE ?? throw new InvalidOperationException("Tolerance Data Editor is not available.");
                if (tde.NumberOfOperands < 1)
                    throw new InvalidOperationException("The current system has no tolerance operands.");

                var tempRoot = Path.Combine(Path.GetTempPath(), "ZemaxMCP-tolerancing");
                Directory.CreateDirectory(tempRoot);
                var ztdPath = Path.Combine(tempRoot, "tol-" + Guid.NewGuid().ToString("N") + ".ztd");

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    RunToleranceTool(
                        system, ztdPath, includeSensitivity, criterion, criterionSampling,
                        criterionComp, criterionCycle, criterionField, monteCarloRuns,
                        monteCarloStatistic, timeoutSeconds, cancellationToken);
                    stopwatch.Stop();

                    if (!File.Exists(ztdPath) || new FileInfo(ztdPath).Length == 0)
                        throw new IOException("OpticStudio tolerancing completed without producing the requested ZTD result file.");

                    return ReadStructuredResults(
                        system, ztdPath, includeSensitivity, criterion, criterionComp, criterionField,
                        monteCarloStatistic, monteCarloRuns, passThreshold, thresholdDirection,
                        maxSensitivityOperands, stopwatch.Elapsed.TotalSeconds, cancellationToken);
                }
                finally
                {
                    try { if (File.Exists(ztdPath)) File.Delete(ztdPath); } catch { }
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void RunToleranceTool(
        IOpticalSystem system,
        string ztdPath,
        bool includeSensitivity,
        Criterions criterion,
        int criterionSampling,
        CriterionComps criterionComp,
        int criterionCycle,
        CriterionFields criterionField,
        int monteCarloRuns,
        MonteCarloStatistics monteCarloStatistic,
        double timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var tolerancing = system.Tools?.OpenTolerancing()
            ?? throw new InvalidOperationException("OpticStudio did not open the Tolerancing tool.");
        try
        {
            tolerancing.SetupMode = includeSensitivity ? SetupModes.Sensitivity : SetupModes.SkipSensitivity;
            tolerancing.Criterion = criterion;
            tolerancing.CriterionSampling = criterionSampling;
            tolerancing.CriterionComp = criterionComp;
            tolerancing.CriterionCycle = criterionCycle;
            tolerancing.CriterionField = criterionField;
            tolerancing.NumberOfRuns = monteCarloRuns;
            tolerancing.NumberToSave = 0;
            tolerancing.IsSaveBestWorstUsed = false;
            tolerancing.IsOverlayGraphicsUsed = false;
            tolerancing.MonteCarloStatistic = monteCarloStatistic;
            tolerancing.OpenDataViewer = false;
            tolerancing.SaveTolDataFile = true;
            tolerancing.TolDataFile = ztdPath;

            if (tolerancing.CriterionSampling != criterionSampling ||
                tolerancing.CriterionCycle != criterionCycle ||
                tolerancing.NumberOfRuns != monteCarloRuns)
                throw new InvalidOperationException("OpticStudio did not preserve one or more requested tolerancing settings.");

            if (!tolerancing.IsValid)
                throw new InvalidOperationException("Tolerancing settings are not valid for the current system.");

            RunBounded(tolerancing, timeoutSeconds, cancellationToken, "Tolerancing");
            if (!tolerancing.Succeeded)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(tolerancing.ErrorMessage)
                        ? "OpticStudio Tolerancing completed without success."
                        : tolerancing.ErrorMessage);
        }
        finally
        {
            StopIfStillRunning(tolerancing);
            tolerancing.Close();
        }
    }

    private static Result ReadStructuredResults(
        IOpticalSystem system,
        string ztdPath,
        bool includeSensitivity,
        Criterions criterion,
        CriterionComps criterionComp,
        CriterionFields criterionField,
        MonteCarloStatistics monteCarloStatistic,
        int monteCarloRuns,
        double? passThreshold,
        string? thresholdDirection,
        int maxSensitivityOperands,
        double runtimeSeconds,
        CancellationToken cancellationToken)
    {
        var viewer = system.Tools?.OpenToleranceDataViewer()
            ?? throw new InvalidOperationException("OpticStudio did not open the Tolerance Data Viewer.");
        try
        {
            viewer.FileName = ztdPath;
            if (!viewer.IsValid)
                throw new InvalidOperationException("Tolerance Data Viewer rejected the generated ZTD file.");
            RunBounded(viewer, 60, cancellationToken, "Tolerance Data Viewer");
            if (!viewer.Succeeded)
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(viewer.ErrorMessage)
                        ? "Tolerance Data Viewer completed without success."
                        : viewer.ErrorMessage);

            var mc = viewer.MonteCarloData;
            var values = mc?.Values;
            var rows = values?.Rows ?? 0;
            var cols = values?.Cols ?? 0;
            var statistics = new List<ColumnStatistics>(Math.Max(0, cols));
            var criterionColumn = -1;
            var expectedCriterionColumn = ExpectedColumnName(criterion);

            for (var column = 0; column < cols; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var metadata = mc!.GetMetadata(column);
                var name = metadata.Name.ToString();
                if (metadata.Name == expectedCriterionColumn && criterionColumn < 0)
                    criterionColumn = column;
                var summary = metadata.SummaryStatistics;
                statistics.Add(new ColumnStatistics(
                    column,
                    name,
                    summary.SampleSize,
                    FiniteOrNull(summary.Minimum),
                    FiniteOrNull(summary.Maximum),
                    FiniteOrNull(summary.Mean),
                    FiniteOrNull(summary.SampleStandardDeviation)));
            }

            ThresholdEvaluation? threshold = null;
            if (passThreshold.HasValue)
            {
                if (criterionColumn < 0)
                    throw new InvalidDataException(
                        $"Monte Carlo data did not contain the expected criterion column {expectedCriterionColumn}.");
                threshold = EvaluateThreshold(
                    values!, criterionColumn, expectedCriterionColumn.ToString(),
                    passThreshold.Value, thresholdDirection!);
            }

            var sensitivity = viewer.SensitivityData;
            var worst = new List<SensitivityOperand>();
            var sensitivityCriteria = sensitivity?.NumberOfCriteria ?? 0;
            var sensitivityCompensators = sensitivity?.NumberOfCompensators ?? 0;
            var sensitivityOperands = sensitivity?.NumberOfResultOperands ?? 0;

            if (includeSensitivity && sensitivity != null && sensitivityCriteria > 0)
            {
                var criterionIndex = FindSensitivityCriterion(sensitivity, criterion);
                if (criterionIndex >= 0)
                {
                    var count = Math.Min(sensitivity.NumberOfResultOperands, 5000);
                    for (var index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var operand = sensitivity.GetOperand(index);
                        var effect = operand.GetEffectOnCriterion(criterionIndex);
                        var minEffect = FiniteOrNull(effect.EstimatedChangeMinimum);
                        var maxEffect = FiniteOrNull(effect.EstimatedChangeMaximum);
                        var worstAbs = WorstAbsolute(minEffect, maxEffect);
                        worst.Add(new SensitivityOperand(
                            index,
                            operand.OperandType.ToString(),
                            operand.Comment ?? string.Empty,
                            FiniteOrNull(operand.Minimum),
                            FiniteOrNull(operand.Maximum),
                            minEffect,
                            maxEffect,
                            worstAbs));
                    }

                    worst = worst
                        .OrderByDescending(item => item.WorstAbsoluteEstimatedChange ?? double.NegativeInfinity)
                        .ThenBy(item => item.OperandIndex)
                        .Take(maxSensitivityOperands)
                        .ToList();
                }
            }

            var summaryText = viewer.Summary;
            var summaryTruncated = !string.IsNullOrEmpty(summaryText) && summaryText.Length > 12000;
            if (summaryTruncated) summaryText = summaryText!.Substring(0, 12000);

            return new Result(
                true,
                null,
                "Completed",
                includeSensitivity ? SetupModes.Sensitivity.ToString() : SetupModes.SkipSensitivity.ToString(),
                CanonicalCriterionName(criterion),
                CanonicalCompName(criterionComp),
                CanonicalFieldName(criterionField),
                monteCarloStatistic.ToString(),
                monteCarloRuns,
                runtimeSeconds,
                string.IsNullOrWhiteSpace(summaryText) ? null : summaryText,
                summaryTruncated,
                rows,
                cols,
                statistics,
                sensitivityCriteria,
                sensitivityCompensators,
                sensitivityOperands,
                worst,
                threshold);
        }
        finally
        {
            StopIfStillRunning(viewer);
            viewer.Close();
        }
    }

    private static ThresholdEvaluation EvaluateThreshold(
        ZOSAPI.Common.IMatrixData values,
        int column,
        string columnName,
        double threshold,
        string direction)
    {
        var finite = 0;
        var nonFinite = 0;
        var passing = 0;
        for (var row = 0; row < values.Rows; row++)
        {
            var value = values.GetValueAt(row, column);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                nonFinite++;
                continue;
            }

            finite++;
            if (direction == "LessOrEqual" ? value <= threshold : value >= threshold)
                passing++;
        }

        return new ThresholdEvaluation(
            columnName,
            threshold,
            direction,
            finite,
            nonFinite,
            passing,
            finite == 0 ? null : (double)passing / finite);
    }

    private static int FindSensitivityCriterion(ISensitivityData sensitivity, Criterions criterion)
    {
        for (var index = 0; index < sensitivity.NumberOfCriteria; index++)
            if (sensitivity.GetCriterion(index).Name == criterion) return index;
        return -1;
    }

    private static string CanonicalCriterionName(Criterions criterion) => ((int)criterion) switch
    {
        0 => "RMSSpotRadius",
        1 => "RMSSpotX",
        2 => "RMSSpotY",
        3 => "RMSWavefront",
        4 => "MeritFunction",
        5 => "GeometricMTFAverage",
        6 => "GeometricMTFTan",
        7 => "GeometricMTFSag",
        8 => "DiffMTFAverage",
        9 => "DiffMTFTan",
        10 => "DiffMTFSag",
        11 => "BoresightError",
        12 => "RMSAngularRadius",
        13 => "RMSAngularX",
        14 => "RMSAngularY",
        _ => throw new ArgumentOutOfRangeException(nameof(criterion), "Unsupported sequential tolerancing criterion.")
    };

    private static string CanonicalCompName(CriterionComps comp) => ((int)comp) switch
    {
        0 => "OptimizeAll_DLS",
        1 => "ParaxialFocus",
        2 => "None",
        3 => "OptimizeAll_OD",
        _ => throw new ArgumentOutOfRangeException(nameof(comp), "Unsupported sequential tolerancing compensation mode.")
    };

    private static string CanonicalFieldName(CriterionFields field) => ((int)field) switch
    {
        0 => "Y_Symmetric",
        1 => "XY_Symmetric",
        2 => "UserDefined",
        _ => throw new ArgumentOutOfRangeException(nameof(field), "Unsupported sequential tolerancing field mode.")
    };

    private static TolerancingColumnName ExpectedColumnName(Criterions criterion) => criterion switch
    {
        Criterions.RMSSpotRadius => TolerancingColumnName.RmsSpotRadius,
        Criterions.RMSSpotX => TolerancingColumnName.RmsSpotX,
        Criterions.RMSSpotY => TolerancingColumnName.RmsSpotY,
        Criterions.RMSWavefront => TolerancingColumnName.RmsWavefrontError,
        Criterions.MeritFunction => TolerancingColumnName.UserMeritFunction,
        Criterions.GeometricMTFAverage => TolerancingColumnName.MtfGeometricAverage,
        Criterions.GeometricMTFTan => TolerancingColumnName.MtfGeometricTangential,
        Criterions.GeometricMTFSag => TolerancingColumnName.MtfGeometricSaggital,
        Criterions.DiffMTFAverage => TolerancingColumnName.MtfDiffractionAverage,
        Criterions.DiffMTFTan => TolerancingColumnName.MtfDiffractionTangential,
        Criterions.DiffMTFSag => TolerancingColumnName.MtfDiffractionSagittal,
        Criterions.BoresightError => TolerancingColumnName.BoresightError,
        Criterions.RMSAngularRadius => TolerancingColumnName.RmsAngularRadius,
        Criterions.RMSAngularX => TolerancingColumnName.RmsAngularX,
        Criterions.RMSAngularY => TolerancingColumnName.RmsAngularY,
        _ => TolerancingColumnName.Unknown
    };

    private static void RunBounded(
        ISystemTool tool,
        double timeoutSeconds,
        CancellationToken cancellationToken,
        string label)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!tool.Run())
            throw new InvalidOperationException($"OpticStudio failed to start {label}.");

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                CancelAndDrain(tool, label);
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (stopwatch.Elapsed.TotalSeconds >= timeoutSeconds)
            {
                CancelAndDrain(tool, label);
                throw new TimeoutException($"{label} exceeded the {timeoutSeconds:G} second wall-clock limit.");
            }

            var remaining = Math.Max(0.01, timeoutSeconds - stopwatch.Elapsed.TotalSeconds);
            var status = tool.WaitWithTimeout(Math.Min(0.25, remaining));
            switch (status)
            {
                case RunStatus.Completed:
                    return;
                case RunStatus.TimedOut:
                    continue;
                case RunStatus.FailedToStart:
                    throw new InvalidOperationException($"{label} failed to start.");
                case RunStatus.InvalidTimeout:
                    throw new InvalidOperationException($"OpticStudio rejected the {label} polling timeout.");
                default:
                    throw new InvalidOperationException($"Unexpected {label} run status: {status}.");
            }
        }
    }

    private static void CancelAndDrain(ISystemTool tool, string label)
    {
        if (tool.IsRunning && tool.CanCancel && !tool.Cancel())
            throw new InvalidOperationException($"OpticStudio rejected {label} cancellation.");
        if (tool.IsRunning && !tool.WaitForCompletion())
            throw new InvalidOperationException($"{label} did not drain after cancellation.");
    }

    private static void StopIfStillRunning(ISystemTool tool)
    {
        if (!tool.IsRunning) return;
        try
        {
            if (tool.CanCancel) tool.Cancel();
            tool.WaitForCompletion();
        }
        catch { }
    }

    private static string? ValidateInputs(
        int criterionSampling,
        int criterionCycle,
        int monteCarloRuns,
        double? passThreshold,
        string? thresholdDirection,
        int maxSensitivityOperands,
        double timeoutSeconds)
    {
        if (criterionSampling < 0 || criterionSampling > 64)
            throw new ArgumentOutOfRangeException(nameof(criterionSampling), "criterionSampling must be between 0 and 64.");
        if (criterionCycle < 0 || criterionCycle > 100)
            throw new ArgumentOutOfRangeException(nameof(criterionCycle), "criterionCycle must be between 0 and 100.");
        if (monteCarloRuns < 0 || monteCarloRuns > MaximumMonteCarloRuns)
            throw new ArgumentOutOfRangeException(nameof(monteCarloRuns), $"monteCarloRuns must be between 0 and {MaximumMonteCarloRuns}.");
        if (maxSensitivityOperands < 1 || maxSensitivityOperands > MaximumReturnedSensitivityOperands)
            throw new ArgumentOutOfRangeException(nameof(maxSensitivityOperands), $"maxSensitivityOperands must be between 1 and {MaximumReturnedSensitivityOperands}.");
        if (double.IsNaN(timeoutSeconds) || double.IsInfinity(timeoutSeconds) || timeoutSeconds < 1 || timeoutSeconds > 7200)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "timeoutSeconds must be finite and between 1 and 7200.");

        if (!passThreshold.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(thresholdDirection))
                throw new ArgumentException("thresholdDirection may only be supplied with passThreshold.", nameof(thresholdDirection));
            return null;
        }

        if (double.IsNaN(passThreshold.Value) || double.IsInfinity(passThreshold.Value))
            throw new ArgumentException("passThreshold must be finite.", nameof(passThreshold));

        var direction = thresholdDirection?.Trim();
        if (direction is not ("LessOrEqual" or "GreaterOrEqual"))
            throw new ArgumentException("thresholdDirection must be LessOrEqual or GreaterOrEqual when passThreshold is supplied.", nameof(thresholdDirection));
        return direction;
    }

    private static T ParseNamedEnum<T>(
        string value,
        string parameterName,
        HashSet<string> allowed) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) || int.TryParse(value, out _) || !allowed.Contains(value.Trim()))
            throw new ArgumentException(
                $"{parameterName} must be one of: {string.Join(", ", allowed.OrderBy(item => item, StringComparer.Ordinal))}.",
                parameterName);
        return Enum.Parse<T>(value.Trim(), ignoreCase: false);
    }

    private static double? FiniteOrNull(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : value;

    private static double? WorstAbsolute(double? first, double? second)
    {
        if (!first.HasValue && !second.HasValue) return null;
        return Math.Max(Math.Abs(first ?? 0), Math.Abs(second ?? 0));
    }

    private static Result EmptyResult(
        bool success,
        string? error,
        string state,
        bool includeSensitivity,
        object criterion,
        object criterionComp,
        object criterionField,
        object monteCarloStatistic,
        int monteCarloRuns,
        string? jobId) => new(
            success,
            error,
            state,
            includeSensitivity ? SetupModes.Sensitivity.ToString() : SetupModes.SkipSensitivity.ToString(),
            criterion.ToString() ?? string.Empty,
            criterionComp.ToString() ?? string.Empty,
            criterionField.ToString() ?? string.Empty,
            monteCarloStatistic.ToString() ?? string.Empty,
            monteCarloRuns,
            0,
            null,
            false,
            0,
            0,
            Array.Empty<ColumnStatistics>(),
            0,
            0,
            0,
            Array.Empty<SensitivityOperand>(),
            null,
            jobId);

    private static readonly HashSet<string> SequentialCriteria = new HashSet<string>(StringComparer.Ordinal)
    {
        "RMSSpotRadius", "RMSSpotX", "RMSSpotY", "RMSWavefront", "MeritFunction",
        "GeometricMTFAverage", "GeometricMTFTan", "GeometricMTFSag",
        "DiffMTFAverage", "DiffMTFTan", "DiffMTFSag", "BoresightError",
        "RMSAngularRadius", "RMSAngularX", "RMSAngularY"
    };

    private static readonly HashSet<string> SequentialComps = new HashSet<string>(StringComparer.Ordinal)
    {
        "OptimizeAll_DLS", "ParaxialFocus", "None", "OptimizeAll_OD"
    };

    private static readonly HashSet<string> SequentialFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "Y_Symmetric", "XY_Symmetric", "UserDefined"
    };

    private static readonly HashSet<string> MonteCarloStatisticsNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Normal", "Uniform", "Parabolic"
    };
}
