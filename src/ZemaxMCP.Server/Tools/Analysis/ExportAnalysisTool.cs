using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Analysis;

namespace ZemaxMCP.Server.Tools.Analysis;

[ZemaxToolType]
public class ExportAnalysisTool
{
    private readonly IZemaxSession _session;

    public ExportAnalysisTool(IZemaxSession session) => _session = session;

    public record ExportResult(
        bool Success,
        string? Error = null,
        string? AnalysisType = null,
        string? ImagePath = null,
        string? TextPath = null);

    [ZemaxTool(Name = "zemax_export_analysis")]
    [Description(
        "Run an explicitly supported OpticStudio analysis and export requested BMP and/or TXT results. " +
        "Supported types: StandardSpot, MatrixSpot, FftMtf, GeometricMtf, FftPsf, HuygensPsf, " +
        "GeometricImageAnalysis, RayFan, OpdFan, WavefrontMap, SeidelDiagram, FieldCurvature, " +
        "LongitudinalAberration, LateralColor, FocalShiftDiagram, Draw2D, Draw3D, FftMtfVsField, " +
        "FftThroughFocusMtf, RelativeIllumination, Interferogram. Aliases such as spot, mtf, psf, ima and layout are accepted. " +
        "The tool does not silently fall back from BMP to TXT. Every requested output must be produced, and existing files are preserved unless overwrite=true.")]
    public async Task<ExportResult> ExecuteAsync(
        [Description("Supported analysis type or documented alias")]
        string analysisType,
        [Description("Optional .BMP path. Omit for text-only export.")]
        string? imagePath = null,
        [Description("Optional .TXT path. Omit for image-only export.")]
        string? textPath = null,
        [Description("Allow replacement of existing requested output files")]
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!TryParseAnalysisType(analysisType, out var analysisId, out var canonicalName))
                return new ExportResult(false, Error: $"Unknown or unsupported analysis type '{analysisType}'.");

            var finalImagePath = NormalizeOutputPath(imagePath, ".BMP", overwrite);
            var finalTextPath = NormalizeOutputPath(textPath, ".TXT", overwrite);
            if (finalImagePath == null && finalTextPath == null)
                return new ExportResult(false, Error: "At least one output must be requested through imagePath or textPath.");

            cancellationToken.ThrowIfCancellationRequested();
            var parameters = new Dictionary<string, object?>
            {
                ["analysisType"] = canonicalName,
                ["imagePath"] = finalImagePath,
                ["textPath"] = finalTextPath,
                ["overwrite"] = overwrite
            };

            return await _session.ExecuteAsync("ExportAnalysis", parameters, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var analysis = system.Analyses.New_Analysis(analysisId)
                    ?? throw new InvalidOperationException($"OpticStudio could not create analysis '{canonicalName}'.");

                try
                {
                    ApplyAnalysisCancellable(analysis, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var results = analysis.GetResults()
                        ?? throw new InvalidOperationException($"Analysis '{canonicalName}' returned no results object.");

                    string? actualImagePath = null;
                    if (finalImagePath != null)
                    {
                        var tempImagePath = CreateSiblingTempPath(finalImagePath, ".BMP");
                        try
                        {
                            if (!AnalysisBmpHelper.TryExportBmp(results, tempImagePath, cancellationToken) ||
                                !File.Exists(tempImagePath) || new FileInfo(tempImagePath).Length == 0)
                            {
                                throw new NotSupportedException($"Analysis '{canonicalName}' did not expose image/grid data that can be exported as BMP by the standalone MCP exporter.");
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            CommitTempFile(tempImagePath, finalImagePath, overwrite);
                            actualImagePath = finalImagePath;
                        }
                        finally
                        {
                            if (File.Exists(tempImagePath)) File.Delete(tempImagePath);
                        }
                    }

                    string? actualTextPath = null;
                    if (finalTextPath != null)
                    {
                        var tempTextPath = CreateSiblingTempPath(finalTextPath, ".TXT");
                        try
                        {
                            var created = results.GetTextFile(tempTextPath);
                            if (!created || !File.Exists(tempTextPath))
                                throw new NotSupportedException($"Analysis '{canonicalName}' does not support text export through IAR_.GetTextFile.");

                            cancellationToken.ThrowIfCancellationRequested();
                            CommitTempFile(tempTextPath, finalTextPath, overwrite);
                            actualTextPath = finalTextPath;
                        }
                        finally
                        {
                            if (File.Exists(tempTextPath)) File.Delete(tempTextPath);
                        }
                    }

                    return new ExportResult(
                        Success: true,
                        AnalysisType: canonicalName,
                        ImagePath: actualImagePath,
                        TextPath: actualTextPath);
                }
                finally
                {
                    try
                    {
                        if (analysis.IsRunning())
                        {
                            analysis.Terminate();
                            analysis.WaitForCompletion();
                        }
                    }
                    finally
                    {
                        analysis.Close();
                    }
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExportResult(false, Error: ex.Message);
        }
    }

    private static void ApplyAnalysisCancellable(IA_ analysis, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        analysis.Apply();
        while (analysis.IsRunning())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                analysis.Terminate();
                analysis.WaitForCompletion();
                cancellationToken.ThrowIfCancellationRequested();
            }
            Thread.Sleep(50);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string? NormalizeOutputPath(string? filePath, string requiredExtension, bool overwrite)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        var fullPath = Path.GetFullPath(filePath.Trim());
        if (!string.Equals(Path.GetExtension(fullPath), requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Output path '{filePath}' must end in {requiredExtension}.", nameof(filePath));

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Output directory does not exist: {directory}");
        if (!overwrite && File.Exists(fullPath))
            throw new IOException($"Output file already exists: {fullPath}. Set overwrite=true to replace it.");

        return fullPath;
    }

    private static string CreateSiblingTempPath(string finalPath, string extension)
    {
        var directory = Path.GetDirectoryName(finalPath)!;
        return Path.Combine(directory, $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp{extension}");
    }

    private static void CommitTempFile(string tempPath, string finalPath, bool overwrite)
    {
        if (overwrite)
        {
            if (File.Exists(finalPath)) File.Replace(tempPath, finalPath, null);
            else File.Move(tempPath, finalPath);
        }
        else
        {
            File.Move(tempPath, finalPath);
        }
    }

    private static bool TryParseAnalysisType(string name, out AnalysisIDM result, out string canonicalName)
    {
        canonicalName = string.Empty;
        result = default;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var key = name.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").ToLowerInvariant();
        (AnalysisIDM Id, string Name)? mapped = key switch
        {
            "standardspot" or "spotdiagram" or "spot" => (AnalysisIDM.StandardSpot, "StandardSpot"),
            "matrixspot" => (AnalysisIDM.MatrixSpot, "MatrixSpot"),
            "fftmtf" or "mtf" => (AnalysisIDM.FftMtf, "FftMtf"),
            "geometricmtf" or "geomtf" => (AnalysisIDM.GeometricMtf, "GeometricMtf"),
            "fftpsf" or "psf" => (AnalysisIDM.FftPsf, "FftPsf"),
            "huygenspsf" => (AnalysisIDM.HuygensPsf, "HuygensPsf"),
            "geometricimageanalysis" or "ima" or "gia" => (AnalysisIDM.GeometricImageAnalysis, "GeometricImageAnalysis"),
            "rayfan" or "transverseray" => (AnalysisIDM.RayFan, "RayFan"),
            "opdfan" or "opd" => (AnalysisIDM.OpticalPathFan, "OpdFan"),
            "wavefrontmap" or "wavefront" => (AnalysisIDM.WavefrontMap, "WavefrontMap"),
            "seidel" or "seideldiagram" => (AnalysisIDM.SeidelDiagram, "SeidelDiagram"),
            "fieldcurvature" or "distortion" => (AnalysisIDM.FieldCurvatureAndDistortion, "FieldCurvature"),
            "longitudinalaberration" => (AnalysisIDM.LongitudinalAberration, "LongitudinalAberration"),
            "lateralcolor" => (AnalysisIDM.LateralColor, "LateralColor"),
            "focalshift" or "chromaticfocalshift" or "focalshiftdiagram" => (AnalysisIDM.FocalShiftDiagram, "FocalShiftDiagram"),
            "draw2d" or "layout2d" or "layout" => (AnalysisIDM.Draw2D, "Draw2D"),
            "draw3d" or "layout3d" => (AnalysisIDM.Draw3D, "Draw3D"),
            "fftmtfvsfield" or "mtfvsfield" => (AnalysisIDM.FftMtfvsField, "FftMtfVsField"),
            "fftthroughfocusmtf" or "throughfocusmtf" => (AnalysisIDM.FftThroughFocusMtf, "FftThroughFocusMtf"),
            "relativeillumination" => (AnalysisIDM.RelativeIllumination, "RelativeIllumination"),
            "interferogram" => (AnalysisIDM.Interferogram, "Interferogram"),
            _ => null
        };

        if (!mapped.HasValue) return false;
        result = mapped.Value.Id;
        canonicalName = mapped.Value.Name;
        return true;
    }
}
