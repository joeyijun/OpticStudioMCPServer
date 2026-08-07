using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Models;
using ZemaxMCP.Core.Session;
using ZemaxMCP.Server.Tools.Base;

namespace ZemaxMCP.Server.Tools.LensData;

[ZemaxToolType]
public class SetFieldsTool
{
    private readonly IZemaxSession _session;

    public SetFieldsTool(IZemaxSession session) => _session = session;

    public record FieldDefinition(double X, double Y, double Weight = 1.0);

    public record SetFieldsResult(
        bool Success,
        string? Error,
        int NumberOfFields,
        List<Field> Fields
    );

    [ZemaxTool(Name = "zemax_set_fields")]
    [Description("Set field point values. Automatically adds or removes fields to match the supplied list.")]
    public async Task<SetFieldsResult> ExecuteAsync(
        [Description("Array of field definitions [{x, y, weight}]")] List<FieldDefinition> fields,
        [Description("Field type: Angle, ObjectHeight, ParaxialImageHeight, RealImageHeight")] string fieldType = "Angle",
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (fields == null || fields.Count == 0)
                throw new ArgumentException("At least one field is required.", nameof(fields));
            for (var i = 0; i < fields.Count; i++)
            {
                var definition = fields[i];
                if (new[] { definition.X, definition.Y, definition.Weight }.Any(double.IsNaN) ||
                    new[] { definition.X, definition.Y, definition.Weight }.Any(double.IsInfinity))
                    throw new ArgumentException($"Field {i + 1} values must be finite.", nameof(fields));
                if (definition.Weight < 0)
                    throw new ArgumentException($"Field {i + 1} weight must be non-negative.", nameof(fields));
            }

            if (string.IsNullOrWhiteSpace(fieldType))
                throw new ArgumentException("Field type is required.", nameof(fieldType));
            var fType = fieldType.Trim().ToLowerInvariant() switch
            {
                "angle" => ZOSAPI.SystemData.FieldType.Angle,
                "objectheight" => ZOSAPI.SystemData.FieldType.ObjectHeight,
                "paraxialimageheight" => ZOSAPI.SystemData.FieldType.ParaxialImageHeight,
                "realimageheight" => ZOSAPI.SystemData.FieldType.RealImageHeight,
                _ => throw new ArgumentException("Field type must be Angle, ObjectHeight, ParaxialImageHeight, or RealImageHeight.", nameof(fieldType))
            };

            var parameters = new Dictionary<string, object?>
            {
                ["fieldCount"] = fields.Count,
                ["fieldType"] = fieldType
            };

            var result = await _session.ExecuteAsync("SetFields", parameters, system =>
            {
                var sysFields = system.SystemData.Fields;
                sysFields.SetFieldType(fType);

                while (sysFields.NumberOfFields < fields.Count)
                    sysFields.AddField(0, 0, 1.0);
                while (sysFields.NumberOfFields > fields.Count)
                    sysFields.RemoveField(sysFields.NumberOfFields);

                var resultFields = new List<Field>();
                for (var i = 0; i < fields.Count; i++)
                {
                    var field = sysFields.GetField(i + 1);
                    field.X = fields[i].X;
                    field.Y = fields[i].Y;
                    field.Weight = fields[i].Weight;

                    resultFields.Add(new Field
                    {
                        Number = i + 1,
                        X = field.X.Sanitize(),
                        Y = field.Y.Sanitize(),
                        Weight = field.Weight.Sanitize()
                    });
                }

                return new SetFieldsResult(true, null, sysFields.NumberOfFields, resultFields);
            }, cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            return new SetFieldsResult(false, ex.Message, 0, new List<Field>());
        }
    }
}
