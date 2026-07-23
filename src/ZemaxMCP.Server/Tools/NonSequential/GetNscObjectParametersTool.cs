using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;
using ZOSAPI;
using ZOSAPI.Editors;
using ZOSAPI.Editors.NCE;

namespace ZemaxMCP.Server.Tools.NonSequential;

[McpServerToolType]
public sealed class GetNscObjectParametersTool
{
    private const int MaximumParametersPerRequest = 100;
    private readonly IZemaxSession _session;

    public GetNscObjectParametersTool(IZemaxSession session) => _session = session;

    public record ObjectParameter(int Number, string Name, string Value, string DataType, bool IsActive, bool IsReadOnly);
    public record Result(bool Success, string? Error, int ObjectNumber, string? ObjectType, int NumberOfAvailableParameters, IReadOnlyList<ObjectParameter> Parameters);

    [McpServerTool(Name = "zemax_get_nsc_object_parameters")]
    [Description("Read type-specific parameters of a non-sequential component (NSC) object. This is read-only and requires a non-sequential system.")]
    public async Task<Result> ExecuteAsync(
        [Description("NSC object number (1-indexed)")] int objectNumber,
        [Description("First type-specific parameter (1-indexed)")] int startParameter = 1,
        [Description("Maximum parameters to return (1-100)")] int maxParameters = 50)
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
                if (system.Mode != SystemType.NonSequential)
                    return new Result(false, "The current system is sequential. Open or create a non-sequential system before using this tool.", objectNumber, null, 0, Array.Empty<ObjectParameter>());

                var nce = system.NCE;
                if (objectNumber > nce.NumberOfObjects)
                    return new Result(false, $"Object {objectNumber} does not exist; the system has {nce.NumberOfObjects} NSC objects.", objectNumber, null, 0, Array.Empty<ObjectParameter>());

                var row = nce.GetObjectAt(objectNumber);
                var names = row.AvailableParameters() ?? Array.Empty<string>();
                var lastParameter = Math.Min(names.Length, startParameter + maxParameters - 1);
                var parameters = new List<ObjectParameter>(Math.Max(0, lastParameter - startParameter + 1));

                for (var number = startParameter; number <= lastParameter; number++)
                {
                    var cell = row.GetObjectCell((ObjectColumn)Enum.Parse(typeof(ObjectColumn), "Par" + number));
                    parameters.Add(new ObjectParameter(
                        number,
                        names[number - 1],
                        ReadCellValue(cell),
                        cell.DataType.ToString(),
                        cell.IsActive,
                        cell.IsReadOnly));
                }

                return new Result(true, null, objectNumber, row.TypeName, names.Length, parameters);
            });
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, objectNumber, null, 0, Array.Empty<ObjectParameter>());
        }
    }

    private static string ReadCellValue(IEditorCell cell) => cell.DataType switch
    {
        CellDataType.Integer => cell.IntegerValue.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
        CellDataType.Double => cell.DoubleValue.ToString("G17", global::System.Globalization.CultureInfo.InvariantCulture),
        _ => cell.Value
    };
}
