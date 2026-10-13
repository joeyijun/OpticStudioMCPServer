using System.Globalization;
using System.Text.Json;
using ZemaxMCP.Core.Session;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.Catalog;

internal static class EngineeringModelReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static string Read(IOpticalSystem system, string modelId)
    {
        if (system.Mode != SystemType.Sequential) throw new ArgumentException("Guarded surface workflow requires a Sequential system; no automatic conversion is performed.");
        var conditions = EngineeringConditionReader.Read(system, modelId);
        if (conditions.OmittedFields != 0 || conditions.OmittedWavelengths != 0) throw new ArgumentException("Guard cannot use truncated field/wavelength evidence.");
        var count = system.LDE.NumberOfSurfaces;
        if (count > 512) throw new ArgumentException("Guarded workflow supports at most 512 LDE rows; no partial model token is issued.");
        var rows = new List<object>();
        for (var i = 0; i < count; i++)
        {
            var r = system.LDE.GetSurfaceAt(i);
            if ((r.Comment?.Length ?? 0) > 2048) throw new ArgumentException("Surface comment exceeds the bounded guard contract.");
            // Canonical raw numeric strings preserve infinity without unsafe JSON
            // or treating a plane/infinite object distance as a finite zero.
            string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            rows.Add(new
            {
                number = i,
                type = r.Type.ToString(),
                radius = N(r.Radius),
                thickness = N(r.Thickness),
                material = r.Material,
                semiDiameter = N(r.SemiDiameter),
                conic = N(r.Conic),
                comment = r.Comment,
                isStop = r.IsStop
            });
        }
        return JsonSerializer.Serialize(new { conditions, rows }, Json);
    }
}
