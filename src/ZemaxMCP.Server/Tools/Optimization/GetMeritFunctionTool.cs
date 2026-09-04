using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI.Editors;
using ZOSAPI.Editors.MFE;

namespace ZemaxMCP.Server.Tools.Optimization;

[ZemaxToolType]
public class GetMeritFunctionTool
{
    private readonly IZemaxSession _session;
    private readonly ILogger<GetMeritFunctionTool> _logger;

    public GetMeritFunctionTool(IZemaxSession session, ILogger<GetMeritFunctionTool> logger)
    {
        _session = session;
        _logger = logger;
    }

    public record MeritCell(
        int Column,
        string DataType,
        int? IntegerValue,
        double? DoubleValue,
        string? StringValue);

    public record MeritOperandInfo(
        int Row,
        string Type,
        string? RowTypeName,
        double Target,
        double Weight,
        double? Value,
        double? Contribution,
        IReadOnlyList<MeritCell> Cells);

    public record GetMeritFunctionResult(
        bool Success,
        string? Error,
        double? TotalMerit,
        int NumberOfOperands,
        int ReturnedOperands,
        IReadOnlyList<MeritOperandInfo> Operands);

    [ZemaxTool(Name = "zemax_get_merit_function")]
    [Description("Read the current Merit Function Editor with typed active parameter cells. Non-finite weighted operand data is reported as an error instead of being converted to zero. Set includeValues=false to avoid recalculating operand values.")]
    public async Task<GetMeritFunctionResult> ExecuteAsync(
        [Description("Calculate the Merit Function and include operand Value/Contribution fields")]
        bool includeValues = true,
        [Description("Start operand row; 0 means row 1")]
        int startRow = 0,
        [Description("End operand row; 0 means the last row")]
        int endRow = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (startRow < 0)
                throw new ArgumentOutOfRangeException(nameof(startRow), "startRow must be 0 or a positive 1-indexed operand row.");
            if (endRow < 0)
                throw new ArgumentOutOfRangeException(nameof(endRow), "endRow must be 0 or a positive 1-indexed operand row.");
            if (startRow > 0 && endRow > 0 && endRow < startRow)
                throw new ArgumentException("endRow must be 0 or greater than or equal to startRow.");
            cancellationToken.ThrowIfCancellationRequested();

            var parameters = new Dictionary<string, object?>
            {
                ["includeValues"] = includeValues,
                ["startRow"] = startRow,
                ["endRow"] = endRow
            };

            return await _session.ExecuteAsync("GetMeritFunction", parameters, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mfe = system.MFE ?? throw new InvalidOperationException("Merit Function Editor is not available.");
                int numberOfOperands = mfe.NumberOfOperands;
                if (numberOfOperands == 0)
                    return new GetMeritFunctionResult(true, null, includeValues ? 0.0 : null, 0, 0, Array.Empty<MeritOperandInfo>());

                int start = startRow == 0 ? 1 : startRow;
                int end = endRow == 0 ? numberOfOperands : endRow;
                if (start > numberOfOperands)
                    throw new ArgumentOutOfRangeException(nameof(startRow), $"startRow {start} exceeds the MFE operand count ({numberOfOperands}).");
                if (end > numberOfOperands)
                    throw new ArgumentOutOfRangeException(nameof(endRow), $"endRow {end} exceeds the MFE operand count ({numberOfOperands}).");

                double? totalMerit = null;
                if (includeValues)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var calculated = mfe.CalculateMeritFunction();
                    ValidateFinite(calculated, "total Merit Function value");
                    totalMerit = calculated;
                }

                var operands = new List<MeritOperandInfo>(end - start + 1);
                for (int rowNumber = start; rowNumber <= end; rowNumber++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = mfe.GetOperandAt(rowNumber)
                        ?? throw new InvalidOperationException($"OpticStudio returned no MFE operand for row {rowNumber}.");
                    if (!row.IsValidRow)
                        throw new InvalidDataException($"MFE row {rowNumber} is not a valid operand row.");

                    double target = row.Target;
                    double weight = row.Weight;
                    ValidateFinite(target, $"target at MFE row {rowNumber}");
                    ValidateFinite(weight, $"weight at MFE row {rowNumber}");
                    if (weight < 0)
                        throw new InvalidDataException($"MFE row {rowNumber} ({row.Type}) has negative weight {weight}.");

                    double? value = null;
                    double? contribution = null;
                    if (includeValues && weight > 0)
                    {
                        var rowValue = row.Value;
                        ValidateFinite(rowValue, $"value at weighted MFE row {rowNumber}");
                        value = rowValue;
                        var diff = target - rowValue;
                        var rowContribution = weight * diff * diff;
                        ValidateFinite(rowContribution, $"contribution at MFE row {rowNumber}");
                        contribution = rowContribution;
                    }

                    var cells = new List<MeritCell>();
                    for (int column = 2; column <= 9; column++)
                    {
                        var cell = row.GetCellAt(column)
                            ?? throw new InvalidOperationException($"MFE row {rowNumber} did not expose cell {column}.");
                        if (!cell.IsActive)
                            continue;
                        cells.Add(ReadCell(rowNumber, column, cell));
                    }

                    operands.Add(new MeritOperandInfo(
                        rowNumber,
                        row.Type.ToString(),
                        row.RowTypeName,
                        target,
                        weight,
                        value,
                        contribution,
                        cells));
                }

                _logger.LogInformation("Read MFE rows {Start}-{End}; {Returned}/{Total} operands returned.", start, end, operands.Count, numberOfOperands);
                return new GetMeritFunctionResult(true, null, totalMerit, numberOfOperands, operands.Count, operands);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetMeritFunction failed.");
            return new GetMeritFunctionResult(false, ex.Message, null, 0, 0, Array.Empty<MeritOperandInfo>());
        }
    }

    private static MeritCell ReadCell(int rowNumber, int column, IEditorCell cell)
    {
        switch (cell.DataType)
        {
            case CellDataType.Integer:
                return new MeritCell(column, cell.DataType.ToString(), cell.IntegerValue, null, null);
            case CellDataType.Double:
                var value = cell.DoubleValue;
                ValidateFinite(value, $"MFE row {rowNumber} cell {column}");
                return new MeritCell(column, cell.DataType.ToString(), null, value, null);
            case CellDataType.String:
                return new MeritCell(column, cell.DataType.ToString(), null, null, cell.Value);
            default:
                throw new NotSupportedException($"MFE row {rowNumber} cell {column} uses unsupported data type {cell.DataType}.");
        }
    }

    private static void ValidateFinite(double value, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"OpticStudio returned non-finite {label}: {value}.");
    }
}
