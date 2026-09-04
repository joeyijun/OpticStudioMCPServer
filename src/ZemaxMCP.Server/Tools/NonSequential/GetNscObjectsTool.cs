using System.ComponentModel;
using ZemaxMCP.Server.Tooling;
using ZemaxMCP.Core.Session;
using ZOSAPI;

namespace ZemaxMCP.Server.Tools.NonSequential;

[ZemaxToolType]
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

    [ZemaxTool(Name = "zemax_get_nsc_objects")]
    [Description("Read non-sequential component (NSC) objects and their positions. This is read-only and requires a non-sequential system.")]
    public async Task<Result> ExecuteAsync(
        [Description("First NSC object number (1-indexed)")] int startObject = 1,
        [Description("Maximum number of objects to return (1-250)")] int maxObjects = 100,
        CancellationToken cancellationToken = default)
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
                cancellationToken.ThrowIfCancellationRequested();
                if (system.Mode != SystemType.NonSequential)
                    return new Result(false, "The current system is sequential. Open or create a non-sequential system before using this tool.", 0, Array.Empty<NscObject>());

                var nce = system.NCE ?? throw new InvalidOperationException("Non-Sequential Component Editor is not available.");
                var numberOfObjects = nce.NumberOfObjects;
                if (numberOfObjects <= 0)
                    return new Result(true, null, 0, Array.Empty<NscObject>());
                if (startObject > numberOfObjects)
                    return new Result(false, $"startObject {startObject} exceeds the {numberOfObjects} objects currently in the NSC editor.", numberOfObjects, Array.Empty<NscObject>());

                var lastObjectLong = Math.Min((long)numberOfObjects, (long)startObject + maxObjects - 1L);
                var lastObject = checked((int)lastObjectLong);
                var objects = new List<NscObject>(lastObject - startObject + 1);

                for (var number = startObject; number <= lastObject; number++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = nce.GetObjectAt(number)
                        ?? throw new InvalidOperationException($"OpticStudio returned no NCE row for object {number}.");
                    ValidateFinite(row.XPosition, number, nameof(row.XPosition));
                    ValidateFinite(row.YPosition, number, nameof(row.YPosition));
                    ValidateFinite(row.ZPosition, number, nameof(row.ZPosition));
                    ValidateFinite(row.TiltAboutX, number, nameof(row.TiltAboutX));
                    ValidateFinite(row.TiltAboutY, number, nameof(row.TiltAboutY));
                    ValidateFinite(row.TiltAboutZ, number, nameof(row.TiltAboutZ));

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
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message, 0, Array.Empty<NscObject>());
        }
    }

    private static void ValidateFinite(double value, int objectNumber, string property)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidDataException($"NSC object {objectNumber} returned non-finite {property}={value}.");
    }
}
