using System.Reflection;
using ZOSAPI.Editors;
using ZOSAPI.Editors.LDE;
using ZemaxMCP.Server.Tools.LensData;

static T Native<T>(params (string Name, object? Value)[] values) where T : class
{
    var proxy = DispatchProxy.Create<T, NativeProperties>();
    ((NativeProperties)(object)proxy).Values = values.ToDictionary(v => v.Name, v => v.Value);
    return proxy;
}
static void Check(bool passed, string name)
{
    if (!passed) throw new InvalidOperationException(name);
    Console.WriteLine("[PASS] " + name);
}
static ISolveData Solve(SolveType type, string property, object native)
    => Native<ISolveData>(("Type", type), (property, native));

var angle = SurfaceSolveReader.Read(Solve(SolveType.MarginalRayAngle, "_S_MarginalRayAngle",
    Native<ISolveMarginalRayAngle>(("Angle", -0.1))));
Check(angle.Angle == -0.1 && angle.Height == -0.1 && angle.PupilZone == null,
    "Cooke marginal-ray angle has Angle, no invented pupil zone");
var height = SurfaceSolveReader.Read(Solve(SolveType.MarginalRayHeight, "_S_MarginalRayHeight",
    Native<ISolveMarginalRayHeight>(("Height", 2.0), ("PupilZone", 0.7))));
Check(height.Height == 2 && height.PupilZone == 0.7, "marginal-ray height");
var chiefAngle = SurfaceSolveReader.Read(Solve(SolveType.ChiefRayAngle, "_S_ChiefRayAngle",
    Native<ISolveChiefRayAngle>(("Angle", 4.0))));
Check(chiefAngle.Angle == 4, "chief-ray angle");
var chiefHeight = SurfaceSolveReader.Read(Solve(SolveType.ChiefRayHeight, "_S_ChiefRayHeight",
    Native<ISolveChiefRayHeight>(("Height", 3.0))));
Check(chiefHeight.Height == 3, "chief-ray height");
var pickup = SurfaceSolveReader.Read(Solve(SolveType.SurfacePickup, "_S_SurfacePickup",
    Native<ISolveSurfacePickup>(("Surface", 2), ("Column", SurfaceColumn.Thickness),
        ("ScaleFactor", -1.0), ("Offset", 0.2))));
Check(pickup.PickupSurface == 2 && pickup.PickupColumn == (int)SurfaceColumn.Thickness &&
    pickup.ScaleFactor == -1 && pickup.Offset == 0.2, "native pickup enum and values");
var edge = SurfaceSolveReader.Read(Solve(SolveType.EdgeThickness, "_S_EdgeThickness",
    Native<ISolveEdgeThickness>(("Thickness", 1.2), ("RadialHeight", 5.0))));
Check(edge.Thickness == 1.2 && edge.RadialHeight == 5, "edge thickness");
var position = SurfaceSolveReader.Read(Solve(SolveType.Position, "_S_Position",
    Native<ISolvePosition>(("FromSurface", 3), ("Length", 12.5))));
Check(position.ReferenceSurface == 3 && position.Position == 12.5,
    "position returns distance separately from reference surface");
var fNumber = SurfaceSolveReader.Read(Solve(SolveType.FNumber, "_S_FNumber",
    Native<ISolveFNumber>(("FNumber", 2.8))));
Check(fNumber.FNumber == 2.8, "f-number");
var center = SurfaceSolveReader.Read(Solve(SolveType.CenterOfCurvature, "_S_CenterOfCurvature",
    Native<ISolveCenterOfCurvature>(("RefSurface", 1))));
Check(center.ReferenceSurface == 1, "center of curvature");
var glass = SurfaceSolveReader.Read(Solve(SolveType.MaterialSubstitute, "_S_MaterialSubstitute",
    Native<ISolveMaterialSubstitute>(("Catalog", "SCHOTT"))), "N-BK7");
Check(glass.Catalog == "SCHOTT" && glass.MaterialName == "N-BK7", "substitution catalog and actual row glass");
var offset = SurfaceSolveReader.Read(Solve(SolveType.MaterialOffset, "_S_MaterialOffset",
    Native<ISolveMaterialOffset>(("NdOffset", 0.01), ("VdOffset", -0.1))));
Check(offset.IndexOffset == 0.01 && offset.AbbeOffset == -0.1, "index and Abbe offsets");
foreach (var type in new[] { SolveType.Fixed, SolveType.Variable, SolveType.Automatic })
    Check(SurfaceSolveReader.Read(Native<ISolveData>(("Type", type))).SolveType == type.ToString(),
        "type-only solve " + type);

public class NativeProperties : DispatchProxy
{
    public Dictionary<string, object?> Values = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method?.Name.StartsWith("get_") == true &&
            Values.TryGetValue(method.Name.Substring(4), out var value)) return value;
        throw new InvalidOperationException("Unexpected native interface member: " + method?.Name);
    }
}
