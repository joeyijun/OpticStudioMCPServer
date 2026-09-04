using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Wizards;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class OptimizationWizardTool
{
    private readonly IZemaxSession _session;

    public OptimizationWizardTool(IZemaxSession session) => _session = session;

    public record OptimizationWizardResult(
        bool Success,
        string? Error,
        int OperandsAdded,
        int FieldsIncluded,
        int ConstraintsAdded,
        string Criterion,
        string Reference,
        double InitialMerit,
        string Summary);

    [ZemaxTool(Name = "zemax_optimization_wizard")]
    [Description("Generate a sequential Merit Function with the current ISEQOptimizationWizard2 API. All settings are validated before mutation; the pre-wizard Merit Function is backed up and restored if clear/apply/cancellation fails.")]
    public async Task<OptimizationWizardResult> ExecuteAsync(
        [Description("Criterion: RMSSpotRadius, RMSSpotRadiusX, RMSSpotRadiusY, RMSWavefront, or PeakToValley.")] string criterion = "RMSSpotRadius",
        [Description("Reference: Centroid, ChiefRay, or Unreferenced.")] string reference = "Centroid",
        [Description("Pupil integration: GaussianQuadrature or RectangularArray.")] string pupilIntegration = "GaussianQuadrature",
        [Description("Gaussian Quadrature rings, 1..20.")] int rings = 3,
        [Description("Rectangular N x N grid size; even integer 4..204.")] int gridSize = 32,
        [Description("Gaussian pupil arms: exactly 6, 8, 10, or 12.")] int arms = 6,
        [Description("Use all fields. If false, the 'field' parameter selects one field.")] bool includeAllFields = true,
        [Description("Field number when includeAllFields=false; 1-indexed.")] int field = 1,
        [Description("Compatibility parameter. ISEQOptimizationWizard2 has no wavelength-selection property, so only 0 is supported.")] int wavelength = 0,
        [Description("Add glass/air boundary constraints using the thickness values below.")] bool addBoundaryConstraints = true,
        [Description("Minimum center thickness for glass boundaries; must be finite and >= 0 when boundaries are enabled.")] double minCenterThickness = 1.0,
        [Description("Maximum center thickness for glass boundaries; must be finite and >= minCenterThickness when boundaries are enabled.")] double maxCenterThickness = 100.0,
        [Description("Minimum edge thickness used for glass edge and air minimum/edge boundaries; must be finite and >= 0 when boundaries are enabled.")] double minEdgeThickness = 0.5,
        [Description("If true, replace the existing Merit Function; otherwise append generated operands after the current last operand.")] bool clearExisting = false,
        CancellationToken cancellationToken = default)
    {
        string criterionName = criterion?.Trim() ?? string.Empty;
        string referenceName = reference?.Trim() ?? string.Empty;
        string integrationName = pupilIntegration?.Trim() ?? string.Empty;

        try
        {
            var mappedCriterion = ParseCriterion(criterionName);
            var mappedReference = ParseReference(referenceName);
            var sampling = ParseSampling(integrationName, rings, arms, gridSize);
            if (field < 1)
                throw new ArgumentOutOfRangeException(nameof(field), "field must be >= 1.");
            if (wavelength != 0)
                throw new NotSupportedException("ISEQOptimizationWizard2 has no wavelength-selection property. wavelength must remain 0; add explicit wavelength-control operands to the MFE when wavelength-specific weighting is required.");
            if (addBoundaryConstraints)
            {
                ValidateNonNegativeFinite(minCenterThickness, nameof(minCenterThickness));
                ValidateNonNegativeFinite(maxCenterThickness, nameof(maxCenterThickness));
                ValidateNonNegativeFinite(minEdgeThickness, nameof(minEdgeThickness));
                if (minCenterThickness > maxCenterThickness)
                    throw new ArgumentException("minCenterThickness cannot exceed maxCenterThickness.");
            }

            var parameters = new Dictionary<string, object?>
            {
                ["criterion"] = mappedCriterion.Name,
                ["reference"] = mappedReference.ToString(),
                ["pupilIntegration"] = sampling.Name,
                ["rings"] = rings,
                ["gridSize"] = gridSize,
                ["arms"] = arms,
                ["includeAllFields"] = includeAllFields,
                ["field"] = field,
                ["wavelength"] = wavelength,
                ["addBoundaryConstraints"] = addBoundaryConstraints,
                ["minCenterThickness"] = minCenterThickness,
                ["maxCenterThickness"] = maxCenterThickness,
                ["minEdgeThickness"] = minEdgeThickness,
                ["clearExisting"] = clearExisting
            };

            return await _session.ExecuteAsync("OptimizationWizard", parameters, system =>
            {
                var mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
                int fieldCount = system.SystemData.Fields.NumberOfFields;
                if (!includeAllFields && field > fieldCount)
                    throw new ArgumentOutOfRangeException(nameof(field), $"field {field} exceeds the system field count ({fieldCount}).");

                int beforeCount = mfe.NumberOfOperands;
                string backupPath = Path.Combine(Path.GetTempPath(), $"zemax_mfe_wizard_{Guid.NewGuid():N}.mf");
                mfe.SaveMeritFunction(backupPath);
                if (!File.Exists(backupPath) || new FileInfo(backupPath).Length == 0)
                    throw new IOException("Could not create the temporary Merit Function backup required for atomic wizard application.");

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clearExisting && beforeCount > 0)
                    {
                        int removed = mfe.RemoveOperandsAt(1, beforeCount);
                        if (removed != beforeCount)
                            throw new InvalidOperationException($"Requested removal of {beforeCount} existing MFE operands, but OpticStudio removed {removed}.");
                    }

                    var wizard = mfe.SEQOptimizationWizard2
                        ?? throw new InvalidOperationException("OpticStudio did not expose ISEQOptimizationWizard2.");
                    wizard.ResetSettings();
                    wizard.Criterion = mappedCriterion.Criterion;
                    wizard.Type = mappedCriterion.Type;
                    wizard.Reference = mappedReference;
                    wizard.XSWeight = mappedCriterion.XSWeight;
                    wizard.YTWeight = mappedCriterion.YTWeight;
                    wizard.UseGaussianQuadrature = sampling.UseGaussian;
                    wizard.UseRectangularArray = !sampling.UseGaussian;
                    if (sampling.UseGaussian)
                    {
                        wizard.Rings = rings;
                        wizard.Arms = sampling.Arms;
                    }
                    else
                    {
                        wizard.GridSizeNxN = gridSize;
                    }

                    wizard.UseAllFields = includeAllFields;
                    if (!includeAllFields) wizard.FieldNumber = field;
                    wizard.UseAllConfigurations = true;
                    wizard.StartAt = clearExisting ? 1 : beforeCount + 1;
                    wizard.OverallWeight = 1.0;

                    wizard.UseGlassBoundaryValues = addBoundaryConstraints;
                    wizard.UseAirBoundaryValues = addBoundaryConstraints;
                    if (addBoundaryConstraints)
                    {
                        wizard.GlassMin = minCenterThickness;
                        wizard.GlassMax = maxCenterThickness;
                        wizard.GlassEdgeThickness = minEdgeThickness;
                        wizard.AirMin = minEdgeThickness;
                        wizard.AirMax = 1000.0;
                        wizard.AirEdgeThickness = minEdgeThickness;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    wizard.Apply();
                    cancellationToken.ThrowIfCancellationRequested();

                    int afterCount = mfe.NumberOfOperands;
                    int operandsAdded = clearExisting ? afterCount : afterCount - beforeCount;
                    if (operandsAdded <= 0)
                        throw new InvalidOperationException("Optimization Wizard completed without adding any Merit Function operands.");

                    double initialMerit = mfe.CalculateMeritFunction();
                    if (double.IsNaN(initialMerit) || double.IsInfinity(initialMerit))
                        throw new InvalidOperationException("Optimization Wizard generated a Merit Function with a non-finite value.");
                    cancellationToken.ThrowIfCancellationRequested();

                    int constraintsAdded = addBoundaryConstraints ? -1 : 0;
                    string constraintNote = addBoundaryConstraints
                        ? "Boundary operands were requested, but Wizard2 does not report their count separately; ConstraintsAdded=-1 means not separately measurable."
                        : "Boundary constraints were disabled.";
                    string summary = $"Wizard2 generated {operandsAdded} operand(s), initial merit {initialMerit:F6}. {constraintNote}";

                    return new OptimizationWizardResult(
                        true,
                        null,
                        operandsAdded,
                        includeAllFields ? fieldCount : 1,
                        constraintsAdded,
                        mappedCriterion.Name,
                        mappedReference.ToString(),
                        initialMerit,
                        summary);
                }
                catch (Exception original)
                {
                    try
                    {
                        mfe.LoadMeritFunction(backupPath);
                    }
                    catch (Exception rollbackException)
                    {
                        throw new InvalidOperationException(
                            $"Optimization Wizard failed and restoring the pre-wizard Merit Function backup also failed. Use the pre-change system safety snapshot for recovery. Original error: {original.Message}; rollback error: {rollbackException.Message}",
                            original);
                    }
                    throw;
                }
                finally
                {
                    try { File.Delete(backupPath); } catch { }
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new OptimizationWizardResult(
                false,
                ex.Message,
                0,
                0,
                0,
                criterionName,
                referenceName,
                0,
                $"Optimization Wizard was not applied: {ex.Message}");
        }
    }

    private static (string Name, CriterionTypes Criterion, OptimizationTypes Type, double XSWeight, double YTWeight) ParseCriterion(string criterion)
    {
        string normalized = NormalizeToken(criterion);
        return normalized switch
        {
            "RMSSPOTRADIUS" => ("RMSSpotRadius", CriterionTypes.Spot, OptimizationTypes.RMS, 1.0, 1.0),
            "RMSSPOTRADIUSX" => ("RMSSpotRadiusX", CriterionTypes.Spot, OptimizationTypes.RMS, 1.0, 0.0),
            "RMSSPOTRADIUSY" => ("RMSSpotRadiusY", CriterionTypes.Spot, OptimizationTypes.RMS, 0.0, 1.0),
            "RMSWAVEFRONT" => ("RMSWavefront", CriterionTypes.Wavefront, OptimizationTypes.RMS, 1.0, 1.0),
            "PEAKTOVALLEY" => ("PeakToValley", CriterionTypes.Wavefront, OptimizationTypes.PTV, 1.0, 1.0),
            _ => throw new ArgumentException("criterion must be RMSSpotRadius, RMSSpotRadiusX, RMSSpotRadiusY, RMSWavefront, or PeakToValley.", nameof(criterion))
        };
    }

    private static ReferenceTypes ParseReference(string reference) => NormalizeToken(reference) switch
    {
        "CENTROID" => ReferenceTypes.Centroid,
        "CHIEFRAY" => ReferenceTypes.ChiefRay,
        "UNREFERENCED" => ReferenceTypes.Unreferenced,
        _ => throw new ArgumentException("reference must be Centroid, ChiefRay, or Unreferenced.", nameof(reference))
    };

    private static (string Name, bool UseGaussian, PupilArmsCount Arms) ParseSampling(string sampling, int rings, int arms, int gridSize)
    {
        switch (NormalizeToken(sampling))
        {
            case "GAUSSIAN":
            case "GAUSSIANQUADRATURE":
                if (rings < 1 || rings > 20)
                    throw new ArgumentOutOfRangeException(nameof(rings), "Gaussian Quadrature rings must be in 1..20.");
                var armEnum = arms switch
                {
                    6 => PupilArmsCount.Arms_6,
                    8 => PupilArmsCount.Arms_8,
                    10 => PupilArmsCount.Arms_10,
                    12 => PupilArmsCount.Arms_12,
                    _ => throw new ArgumentOutOfRangeException(nameof(arms), "Gaussian arms must be exactly 6, 8, 10, or 12.")
                };
                return ("GaussianQuadrature", true, armEnum);

            case "RECTANGULAR":
            case "RECTANGULARARRAY":
                if (gridSize < 4 || gridSize > 204 || (gridSize & 1) != 0)
                    throw new ArgumentOutOfRangeException(nameof(gridSize), "Rectangular gridSize must be an even integer in 4..204.");
                return ("RectangularArray", false, PupilArmsCount.Arms_6);

            default:
                throw new ArgumentException("pupilIntegration must be GaussianQuadrature or RectangularArray.", nameof(sampling));
        }
    }

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static void ValidateNonNegativeFinite(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, $"{name} must be finite and >= 0.");
    }
}
