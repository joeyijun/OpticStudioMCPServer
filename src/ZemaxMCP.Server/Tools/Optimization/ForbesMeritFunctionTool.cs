using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Editors;
using ZOSAPI.Editors.MFE;

namespace ZemaxMCP.Server.Tools.Optimization;

/// <summary>
/// Creates a merit function using the existing Forbes 1988 Gaussian-quadrature
/// sampling implementation. This tool deliberately treats MFE generation as a
/// transaction: the complete original MFE is restored if generation, merit
/// calculation, or cancellation fails.
/// </summary>
[ZemaxToolType]
public class ForbesMeritFunctionTool
{
    private readonly IZemaxSession _session;

    public ForbesMeritFunctionTool(IZemaxSession session) => _session = session;

    public record ForbesMeritFunctionResult(
        bool Success,
        string? Error,
        int TotalOperandsAdded,
        int ConfigurationsIncluded,
        int FieldsIncluded,
        int WavelengthsIncluded,
        int PupilSamplesPerField,
        double InitialMerit,
        List<string> Summary
    );

    [ZemaxTool(Name = "zemax_forbes_merit_function")]
    [Description("Create a Forbes-sampled merit function with explicit OPDX/OPDC/OPDM operands. Inputs are validated before mutation and the original MFE is restored if generation, calculation, or cancellation fails.")]
    public async Task<ForbesMeritFunctionResult> ExecuteAsync(
        [Description("OPD operand type: OPDX, OPDC, or OPDM")]
        string operandType = "OPDX",
        [Description("Number of radial Gaussian rings (1-6)")]
        int rings = 3,
        [Description("Number of angular samples per ring (1-12)")]
        int arms = 6,
        [Description("Include every defined wavelength. When true, wavelength must remain 0 because it would otherwise be ignored.")]
        bool includeAllWavelengths = true,
        [Description("Specific wavelength when includeAllWavelengths=false; 0 requests the operand's polychromatic convention")]
        int wavelength = 0,
        [Description("Include all configurations in multi-configuration systems")]
        bool includeAllConfigurations = true,
        [Description("Remove existing MFE operands before constructing the Forbes set")]
        bool clearExisting = true,
        [Description("Insert BLNK separator rows for organization. No comment text is written into the BLNK rows.")]
        bool addComments = true,
        [Description("Use the available Radau radial table for axial fields. The current implementation supports Radau for rings 1-5.")]
        bool useRadauForAxial = false,
        [Description("Exploit Y-symmetry for off-axis fields with Hx=0")]
        bool assumeSymmetry = false,
        CancellationToken cancellationToken = default)
    {
        string normalizedOperandType = operandType?.Trim().ToUpperInvariant() ?? string.Empty;
        try
        {
            var opType = ParseOperandType(normalizedOperandType);
            if (rings < 1 || rings > 6)
                throw new ArgumentOutOfRangeException(nameof(rings), "rings must be between 1 and 6.");
            if (arms < 1 || arms > 12)
                throw new ArgumentOutOfRangeException(nameof(arms), "arms must be between 1 and 12.");
            if (useRadauForAxial && !ForbesPupilSampling.RadauParameters.ContainsKey(rings))
                throw new ArgumentOutOfRangeException(nameof(rings), "useRadauForAxial=true is supported only for rings 1-5 by the current Forbes Radau table.");
            if (wavelength < 0)
                throw new ArgumentOutOfRangeException(nameof(wavelength), "wavelength must be 0 or a positive wavelength number.");
            if (includeAllWavelengths && wavelength != 0)
                throw new ArgumentException("wavelength must be 0 when includeAllWavelengths=true; a nonzero value would be ignored.", nameof(wavelength));
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new Dictionary<string, object?>
            {
                ["operandType"] = normalizedOperandType,
                ["rings"] = rings,
                ["arms"] = arms,
                ["includeAllWavelengths"] = includeAllWavelengths,
                ["wavelength"] = wavelength,
                ["includeAllConfigurations"] = includeAllConfigurations,
                ["clearExisting"] = clearExisting,
                ["addComments"] = addComments,
                ["useRadauForAxial"] = useRadauForAxial,
                ["assumeSymmetry"] = assumeSymmetry
            };

            return await _session.ExecuteAsync("ForbesMeritFunction", parameters, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
                var fields = system.SystemData?.Fields ?? throw new InvalidOperationException("Field data is not available.");
                var wavelengths = system.SystemData?.Wavelengths ?? throw new InvalidOperationException("Wavelength data is not available.");
                var mce = system.MCE ?? throw new InvalidOperationException("Multi-Configuration Editor is not available.");
                if (fields.NumberOfFields < 1)
                    throw new InvalidOperationException("The current system has no defined fields.");
                if (wavelengths.NumberOfWavelengths < 1)
                    throw new InvalidOperationException("The current system has no defined wavelengths.");
                if (!includeAllWavelengths && wavelength > wavelengths.NumberOfWavelengths)
                    throw new ArgumentOutOfRangeException(nameof(wavelength), $"wavelength must be 0 or between 1 and {wavelengths.NumberOfWavelengths}.");
                if (mce.NumberOfConfigurations < 1)
                    throw new InvalidOperationException("The current system has no active configuration.");

                var backupPath = Path.Combine(Path.GetTempPath(), $"ZemaxMCP_Forbes_MFE_{Guid.NewGuid():N}.MF");
                var backupCreated = false;
                try
                {
                    mfe.SaveMeritFunction(backupPath);
                    backupCreated = File.Exists(backupPath);
                    if (!backupCreated)
                        throw new IOException("Unable to create a temporary MFE rollback file before Forbes generation.");

                    cancellationToken.ThrowIfCancellationRequested();
                    int operandsBefore = mfe.NumberOfOperands;
                    int totalOperandsAdded = 0;
                    var summary = new List<string>();

                    if (clearExisting)
                        ClearExistingOperands(mfe, cancellationToken);

                    if (mfe.NumberOfOperands == 1 && mfe.GetOperandAt(1).Type != MeritOperandType.DMFS)
                    {
                        var dmfsRow = mfe.InsertNewOperandAt(1)
                            ?? throw new InvalidOperationException("OpticStudio did not return the inserted DMFS row.");
                        ChangeTypeChecked(dmfsRow, MeritOperandType.DMFS, "DMFS");
                        totalOperandsAdded++;
                    }

                    var wavelengthList = BuildWavelengthList(wavelengths.NumberOfWavelengths, includeAllWavelengths, wavelength);
                    var configList = BuildConfigurationList(mce.NumberOfConfigurations, mce.CurrentConfiguration, includeAllConfigurations);
                    var fieldData = ReadFieldData(fields, cancellationToken);
                    var wavelengthWeights = ReadWavelengthWeights(wavelengths, wavelengthList, cancellationToken);

                    if (addComments)
                    {
                        AddBlankRow(mfe);
                        totalOperandsAdded++;
                    }

                    int pupilSamplesPerField = 0;
                    foreach (int configNum in configList)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (configList.Count > 1)
                        {
                            var confRow = mfe.AddOperand()
                                ?? throw new InvalidOperationException("OpticStudio did not return the added CONF row.");
                            ChangeTypeChecked(confRow, MeritOperandType.CONF, "CONF");
                            SetIntegerCell(confRow, 2, configNum, "CONF configuration number");
                            totalOperandsAdded++;
                            if (addComments)
                            {
                                AddBlankRow(mfe);
                                totalOperandsAdded++;
                            }
                        }

                        foreach (var fieldInfo in fieldData)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (addComments)
                            {
                                AddBlankRow(mfe);
                                totalOperandsAdded++;
                            }

                            var pupilSamples = BuildPupilSamples(fieldInfo.Hx, fieldInfo.Hy, rings, arms, useRadauForAxial, assumeSymmetry);
                            if (pupilSamples.Count == 0)
                                throw new InvalidOperationException($"Forbes sampling produced no pupil points for field {fieldInfo.Number}.");
                            if (pupilSamplesPerField == 0)
                                pupilSamplesPerField = pupilSamples.Count;

                            foreach (int waveNum in wavelengthList)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                double waveWeight = wavelengthWeights[waveNum];
                                foreach (var sample in pupilSamples)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    var row = mfe.AddOperand()
                                        ?? throw new InvalidOperationException("OpticStudio did not return an added Forbes operand row.");
                                    ChangeTypeChecked(row, opType, normalizedOperandType);
                                    SetIntegerCell(row, 2, 0, $"{normalizedOperandType} sampling selector");
                                    SetIntegerCell(row, 3, waveNum, $"{normalizedOperandType} wavelength");
                                    SetDoubleCell(row, 4, fieldInfo.Hx, $"{normalizedOperandType} Hx");
                                    SetDoubleCell(row, 5, fieldInfo.Hy, $"{normalizedOperandType} Hy");
                                    SetDoubleCell(row, 6, sample.Px, $"{normalizedOperandType} Px");
                                    SetDoubleCell(row, 7, sample.Py, $"{normalizedOperandType} Py");
                                    row.Target = 0;
                                    var combinedWeight = fieldInfo.Weight * waveWeight * sample.Weight;
                                    if (!IsFinite(combinedWeight) || combinedWeight < 0)
                                        throw new InvalidDataException($"Forbes generated invalid operand weight {combinedWeight}.");
                                    row.Weight = combinedWeight;
                                    totalOperandsAdded++;
                                }
                            }
                        }
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    double initialMerit = mfe.CalculateMeritFunction();
                    if (!IsFinite(initialMerit))
                        throw new InvalidDataException($"Generated Forbes merit function returned non-finite merit {initialMerit}.");

                    summary.Add($"Operand type: {normalizedOperandType}");
                    summary.Add($"Forbes GQ: {rings} rings, {arms} arms");
                    summary.Add($"Radau for axial fields: {useRadauForAxial}");
                    summary.Add($"Assume Y-symmetry: {assumeSymmetry}");
                    summary.Add($"Pupil samples in first processed field: {pupilSamplesPerField}");
                    summary.Add($"Configurations: {configList.Count}");
                    summary.Add($"Fields: {fieldData.Count}");
                    summary.Add($"Wavelengths: {wavelengthList.Count}");
                    summary.Add($"Operands before generation: {operandsBefore}");
                    summary.Add($"Operands added: {totalOperandsAdded}");
                    summary.Add($"Initial merit: {initialMerit:E4}");

                    return new ForbesMeritFunctionResult(
                        true, null, totalOperandsAdded, configList.Count, fieldData.Count,
                        wavelengthList.Count, pupilSamplesPerField, initialMerit, summary);
                }
                catch
                {
                    if (backupCreated)
                    {
                        try
                        {
                            mfe.LoadMeritFunction(backupPath);
                        }
                        catch (Exception rollbackException)
                        {
                            throw new InvalidOperationException(
                                "Forbes merit generation failed and restoring the original MFE also failed. Use the pre-operation safety snapshot for recovery.",
                                rollbackException);
                        }
                    }
                    throw;
                }
                finally
                {
                    if (File.Exists(backupPath)) File.Delete(backupPath);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ForbesMeritFunctionResult(false, ex.Message, 0, 0, 0, 0, 0, 0, new List<string>());
        }
    }

    private static MeritOperandType ParseOperandType(string operandType) => operandType switch
    {
        "OPDX" => MeritOperandType.OPDX,
        "OPDC" => MeritOperandType.OPDC,
        "OPDM" => MeritOperandType.OPDM,
        _ => throw new ArgumentException($"Invalid operand type '{operandType}'. Valid options: OPDX, OPDC, OPDM.", nameof(operandType))
    };

    private static void ClearExistingOperands(IMeritFunctionEditor mfe, CancellationToken cancellationToken)
    {
        while (mfe.NumberOfOperands > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = mfe.NumberOfOperands;
            if (!mfe.RemoveOperandAt(row))
                throw new InvalidOperationException($"OpticStudio failed to remove existing MFE operand {row} while clearing the editor.");
        }
    }

    private static List<int> BuildWavelengthList(int wavelengthCount, bool includeAllWavelengths, int wavelength)
    {
        if (includeAllWavelengths)
            return Enumerable.Range(1, wavelengthCount).ToList();
        return new List<int> { wavelength };
    }

    private static List<int> BuildConfigurationList(int configurationCount, int currentConfiguration, bool includeAllConfigurations)
    {
        if (includeAllConfigurations && configurationCount > 1)
            return Enumerable.Range(1, configurationCount).ToList();
        if (currentConfiguration < 1 || currentConfiguration > configurationCount)
            throw new InvalidDataException($"Current configuration {currentConfiguration} is outside 1..{configurationCount}.");
        return new List<int> { currentConfiguration };
    }

    private static List<FieldInfo> ReadFieldData(dynamic fields, CancellationToken cancellationToken)
    {
        var raw = new List<(int Number, double X, double Y, double Weight)>();
        double maxFieldExtent = 0;
        double totalWeight = 0;
        for (int number = 1; number <= fields.NumberOfFields; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = fields.GetField(number);
            double x = field.X;
            double y = field.Y;
            double weight = field.Weight;
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(weight) || weight < 0)
                throw new InvalidDataException($"Field {number} returned invalid X/Y/weight data.");
            raw.Add((number, x, y, weight));
            maxFieldExtent = Math.Max(maxFieldExtent, Math.Sqrt(x * x + y * y));
            totalWeight += weight;
        }
        bool useEqualFieldWeights = totalWeight <= 0;
        if (useEqualFieldWeights) totalWeight = raw.Count;
        if (maxFieldExtent <= 0) maxFieldExtent = 1.0;

        return raw.Select(item => new FieldInfo(
            item.Number,
            item.X / maxFieldExtent,
            item.Y / maxFieldExtent,
            useEqualFieldWeights ? 1.0 / raw.Count : item.Weight / totalWeight)).ToList();
    }

    private static Dictionary<int, double> ReadWavelengthWeights(dynamic wavelengths, IReadOnlyList<int> wavelengthList, CancellationToken cancellationToken)
    {
        if (wavelengthList.Count == 1 && wavelengthList[0] == 0)
            return new Dictionary<int, double> { [0] = 1.0 };

        var raw = new Dictionary<int, double>();
        double total = 0;
        foreach (int number in wavelengthList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wavelength = wavelengths.GetWavelength(number);
            double weight = wavelength.Weight;
            if (!IsFinite(weight) || weight < 0)
                throw new InvalidDataException($"Wavelength {number} returned invalid weight {weight}.");
            raw[number] = weight;
            total += weight;
        }
        if (total <= 0)
            return wavelengthList.ToDictionary(number => number, _ => 1.0 / wavelengthList.Count);
        return raw.ToDictionary(entry => entry.Key, entry => entry.Value / total);
    }

    private static List<ForbesPupilSampling.PupilSamplePoint> BuildPupilSamples(
        double hx, double hy, int rings, int arms, bool useRadauForAxial, bool assumeSymmetry)
    {
        bool isAxial = Math.Abs(hx) < 1e-6 && Math.Abs(hy) < 1e-6;
        if (isAxial)
            return ForbesPupilSampling.GenerateAxialSamplePoints(rings, useRadauForAxial);
        if (assumeSymmetry && Math.Abs(hx) < 1e-6)
            return ForbesPupilSampling.GenerateSymmetricSamplePoints(rings, arms, useRadau: false);
        return ForbesPupilSampling.GenerateSamplePoints(rings, arms, useRadau: false);
    }

    private static void AddBlankRow(IMeritFunctionEditor mfe)
    {
        var row = mfe.AddOperand() ?? throw new InvalidOperationException("OpticStudio did not return an added BLNK row.");
        ChangeTypeChecked(row, MeritOperandType.BLNK, "BLNK");
    }

    private static void ChangeTypeChecked(IMFERow row, MeritOperandType type, string label)
    {
        if (!row.ChangeType(type))
            throw new InvalidOperationException($"OpticStudio rejected MFE operand type {label}.");
    }

    private static void SetIntegerCell(IMFERow row, int column, int value, string label)
    {
        var cell = row.GetCellAt(column) ?? throw new InvalidOperationException($"{label} cell {column} is unavailable.");
        if (cell.DataType != CellDataType.Integer)
            throw new InvalidOperationException($"{label} expected Integer cell {column}, but OpticStudio reports {cell.DataType}.");
        cell.IntegerValue = value;
    }

    private static void SetDoubleCell(IMFERow row, int column, double value, string label)
    {
        if (!IsFinite(value))
            throw new InvalidDataException($"{label} is non-finite ({value}).");
        var cell = row.GetCellAt(column) ?? throw new InvalidOperationException($"{label} cell {column} is unavailable.");
        if (cell.DataType != CellDataType.Double)
            throw new InvalidOperationException($"{label} expected Double cell {column}, but OpticStudio reports {cell.DataType}.");
        cell.DoubleValue = value;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private sealed record FieldInfo(int Number, double Hx, double Hy, double Weight);
}
