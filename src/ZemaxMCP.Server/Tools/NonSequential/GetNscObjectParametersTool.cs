using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI;
using ZOSAPI.Editors;
using ZOSAPI.Editors.NCE;

namespace ZemaxMCP.Server.Tools.NonSequential;

[ZemaxToolType]
public sealed class GetNscObjectParametersTool
{
    private const int MaximumParametersPerRequest = 100;
    private readonly IZemaxSession _session;

    public GetNscObjectParametersTool(IZemaxSession session) => _session = session;

    public record ObjectParameter(
        int Number,
        string Name,
        string Value,
        string DataType,
        bool IsActive,
        bool IsReadOnly,
        int? IntegerValue = null,
        double? DoubleValue = null,
        string? StringValue = null);

    public record Result(bool Success, string? Error, int ObjectNumber, string? ObjectType, int NumberOfAvailableParameters, IReadOnlyList<ObjectParameter> Parameters);

    [ZemaxTool(Name = "zemax_get_nsc_object_parameters")]
    [Description("Read type-specific parameters of a non-sequential component (NSC) object with type-preserving integer/double/string values. This is read-only and requires a non-sequential system.")]
    public async Task<Result> ExecuteAsync(
        [Description("NSC object number (1-indexed)")] int objectNumber,
        [Description("First type-specific parameter (1-indexed)")] int startParameter = 1,
        [Description("Maximum parameters to return (1-100)")] int maxParameters = 50,
        CancellationToken cancellationToken = default)
    {
        if (objectNumber < 1)
            return new Result(false, "objectNumber must be at least 1.", objectNumber, null, 0, Array.Empty<ObjectParameter>());
        if (startParameter < 1)
            return new Result(false, "startParameter must be at least 1.", objectNumber, null, 0, Array.Empty<ObjectParameter>());
        if (maxParameters is < 1 or > MaximumParametersPerRequest)
            return new Result(false, $"maxParameters must be between 1 and {MaximumParametersPerRequest}.", objectNumber, null, 0, Array.Empty<ObjectParameter>());

        try
        {
            return await _session.ExecuteAsync("GetNscObjectParameters", new Dictionary<string, object?>
            {
                ["objectNumber"] = objectNumber,
                ["startParameter"] = startParameter,
                ["maxParameters"] = maxParameters
            }, system =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (system.Mode != SystemType.NonSequential)
                    return new Result(false, "The current system is sequential. Open or create a non-sequential system before using this tool.", objectNumber, null, 0, Array.Empty<ObjectParameter>());

                var nce = system.NCE ?? throw new InvalidOperationException("Non-Sequential Component Editor is not available.");
                if (objectNumber > nce.NumberOfObjects)
                    return new Result(false, $"Object {objectNumber} does not exist; the system has {nce.NumberOfObjects} NSC objects.", objectNumber, null, 0, Array.Empty<ObjectParameter>());

                var row = nce.GetObjectAt(objectNumber)
                    ?? throw new InvalidOperationException($"OpticStudio returned no NCE row for object {objectNumber}.");
                var names = row.AvailableParameters() ?? Array.Empty<string>();
                if (names.Length == 0)
                    return new Result(true, null, objectNumber, row.TypeName, 0, Array.Empty<ObjectParameter>());
                if (startParameter > names.Length)
                    return new Result(false, $"startParameter {startParameter} exceeds the {names.Length} parameters available for object {objectNumber} ({row.TypeName}).", objectNumber, row.TypeName, names.Length, Array.Empty<ObjectParameter>());

                var lastParameter = Math.Min(names.Length, checked(startParameter + maxParameters - 1));
                var parameters = new List<ObjectParameter>(lastParameter - startParameter + 1);

                for (var number = startParameter; number <= lastParameter; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Enum.TryParse<ObjectColumn>("Par" + number, ignoreCase: false, out var column) ||
                        !Enum.IsDefined(typeof(ObjectColumn), column))
                    {
                        throw new NotSupportedException($"Object {objectNumber} exposes parameter {number} ('{names[number - 1]}'), but this ZOS-API ObjectColumn enum does not expose Par{number}.");
                    }

                    var cell = row.GetObjectCell(column)
                        ?? throw new InvalidOperationException($"OpticStudio returned no editor cell for object {objectNumber} parameter {number}.");
                    parameters.Add(ReadParameter(number, names[number - 1], cell));
                }

                return new Result(true, null, objectNumber, row.TypeName, names.Length, parameters);
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, objectNumber, null, 0, Array.Empty<ObjectParameter>());
        }
    }

    private static ObjectParameter ReadParameter(int number, string name, IEditorCell cell)
    {
        switch (cell.DataType)
        {
            case CellDataType.Integer:
                var integerValue = cell.IntegerValue;
                return new ObjectParameter(number, name,
                    integerValue.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
                    cell.DataType.ToString(), cell.IsActive, cell.IsReadOnly,
                    IntegerValue: integerValue);

            case CellDataType.Double:
                var doubleValue = cell.DoubleValue;
                if (double.IsNaN(doubleValue) || double.IsInfinity(doubleValue))
                    throw new InvalidDataException($"NSC parameter {number} ('{name}') returned non-finite value {doubleValue}.");
                return new ObjectParameter(number, name,
                    doubleValue.ToString("G17", global::System.Globalization.CultureInfo.InvariantCulture),
                    cell.DataType.ToString(), cell.IsActive, cell.IsReadOnly,
                    DoubleValue: doubleValue);

            case CellDataType.String:
                var stringValue = cell.Value;
                return new ObjectParameter(number, name, stringValue,
                    cell.DataType.ToString(), cell.IsActive, cell.IsReadOnly,
                    StringValue: stringValue);

            default:
                throw new NotSupportedException($"NSC parameter {number} ('{name}') uses unsupported cell data type {cell.DataType}.");
        }
    }
}
