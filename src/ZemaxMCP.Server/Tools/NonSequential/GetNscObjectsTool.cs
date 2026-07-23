using System.ComponentModel;
using ModelContextProtocol.Server;
using ZemaxMCP.Core.Session;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.NonSequential;

[McpServerToolType]
public sealed class GetNscObjectsTool
{
    private const int MaximumObjectsPerRequest = 250;
    private readonly IZemaxSession _session;

    public GetNscObjectsTool(IZemaxSession session) => _session = session;

    public record NscObject(
        int Number,
        long Id,
        string Type,
        string? Comment,
        string? Material,
        bool IsActive,
        bool IsDetector,
        int ReferenceObject,
        int InsideOf,
        double X,
        double Y,
        double Z,
        double TiltX,
        double TiltY,
        double TiltZ);

    public record Result(bool Success, string? Error, int NumberOfObjects, IReadOnlyList<NscObject> Objects);

    [McpServerTool(Name = "zemax_get_nsc_objects")]
    [Description("Read non-sequential component (NSC) objects and their positions. This is read-only and requires a non-sequential system.")]
    public async Task<Result> ExecuteAsync(
        [Description("First NSC object number (1-indexed)")] int startObject = 1,
        [Description("Maximum number of objects to return (1-250)")] int maxObjects = 100)
    {
        if (startObject < 1)
            return new Result(false, "startObject must be at least 1.", 0, Array.Empty<NscObject>());
        if (maxObjects is < 1 or > MaximumObjectsPerRequest)
            return new Result(false, $"maxObjects must be between 1 and {MaximumObjectsPerRequest}.", 0, Array.Empty<NscObject>());

        try
        {
            return await _session.ExecuteAsync("GetNscObjects", new Dictionary<string, object?>
            {
                ["startObject"] = startObject,
                ["maxObjects"] = maxObjects
            }, system =>
            {
                if (system.Mode != SystemType.NonSequential)
                    return new Result(false, "The current system is sequential. Open or create a non-sequential system before using this tool.", 0, Array.Empty<NscObject>());

                var nce = system.NCE;
                var numberOfObjects = nce.NumberOfObjects;
                var lastObject = Math.Min(numberOfObjects, startObject + maxObjects - 1);
                var objects = new List<NscObject>(Math.Max(0, lastObject - startObject + 1));

                for (var number = startObject; number <= lastObject; number++)
                {
                    var row = nce.GetObjectAt(number);
                    objects.Add(new NscObject(
                        row.ObjectNumber,
                        row.ObjectId,
                        row.TypeName,
                        row.Comment,
                        row.Material,
                        row.IsActive,
                        row.TypeData.ObjectIsADetector,
                        row.RefObject,
                        row.InsideOf,
                        row.XPosition,
                        row.YPosition,
                        row.ZPosition,
                        row.TiltAboutX,
                        row.TiltAboutY,
                        row.TiltAboutZ));
                }

                return new Result(true, null, numberOfObjects, objects);
            });
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, 0, Array.Empty<NscObject>());
        }
    }
}
