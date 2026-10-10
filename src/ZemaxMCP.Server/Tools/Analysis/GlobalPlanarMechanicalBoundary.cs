namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// A measured *planar* polygon in OpticStudio global XYZ, including folded
/// coordinate systems. No implicit projected cutting, no COM and no CAD import.
/// Only ray intercepts close enough to that physical plane are classified.
/// </summary>
public static class GlobalPlanarMechanicalBoundary
{
    public sealed record Assessment(
        int SurvivingRays, int PlaneMatchedRays, int PlaneUnmatchedRays,
        int InsideRays, int OutsideRays, double? MinimumSignedEdgeClearance,
        double? OutsideFractionOfPlaneMatched, double MaximumPlaneDeviation,
        double PlaneTolerance, string CoordinateFrame, string Interpretation);

    internal sealed record Basis(double[] Origin, double[] AxisU,
        double[] AxisV, double[] Normal, double[][] Vertices2D);

    private static double Dot(double[] a,double[] b) =>
        a[0]*b[0]+a[1]*b[1]+a[2]*b[2];
    private static double[] Subtract(double[] a,double[] b) =>
        new[]{a[0]-b[0],a[1]-b[1],a[2]-b[2]};
    private static double[] Cross(double[] a,double[] b) =>
        new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};
    private static double Norm(double[] v) => Math.Sqrt(Dot(v,v));
    private static double[] Unit(double[] v)
    {
        var length=Norm(v);
        if(!double.IsFinite(length)||length<=1e-12)
            throw new ArgumentException("Mechanical polygon is degenerate.");
        return v.Select(value=>value/length).ToArray();
    }

    internal static Basis Build(double[][] vertices,double tolerance)
    {
        if(!double.IsFinite(tolerance) || tolerance<=0 || tolerance>1e6 ||
            vertices is not {Length:>=3 and <=64} ||
            vertices.Any(v=>v==null||v.Length!=3||
                v.Any(x=>!double.IsFinite(x)||Math.Abs(x)>1e9)))
            throw new ArgumentException("Expected 3..64 finite global [x,y,z] vertices and positive finite plane tolerance.");
        var origin=vertices[0];
        var u=Unit(Subtract(vertices[1],origin));
        double[]? normal=null;
        for(var i=2;i<vertices.Length;i++)
        {
            var edge=Subtract(vertices[i],origin);
            var normalVector=Cross(u,edge);
            if(Norm(normalVector)>1e-10) { normal=Unit(normalVector);break; }
        }
        if(normal==null)
            throw new ArgumentException("All mechanical polygon vertices are collinear.");
        var v=Cross(normal,u);
        // A 3-D CAD outline is only meaningful as a planar aperture when
        // the vertices actually lie in ONE plane. Never silently flatten it.
        var xy=new double[vertices.Length][];
        for(var i=0;i<vertices.Length;i++)
        {
            var relative=Subtract(vertices[i],origin);
            var outOfPlane=Math.Abs(Dot(relative,normal));
            if(outOfPlane>1e-8*Math.Max(1,Norm(relative)))
                throw new ArgumentException("Mechanical CAD polygon is not coplanar; a plane boundary cannot represent it.");
            xy[i]=new[]{Dot(relative,u),Dot(relative,v)};
        }
        MechanicalFootprintBoundary.Validate(xy);
        return new Basis(origin,u,v,normal,xy);
    }

    internal static Assessment Assess(double[][] polygon,double[][] rotation,double[] origin,
        IReadOnlyList<(double X,double Y,double Z)> localRayIntersections,double planeTolerance)
    {
        var plane=Build(polygon,planeTolerance);
        // Reuse the official local-to-global LDE matrix implementation;
        // local Z contains the actual traced sag, not a tangent-plane guess.
        var globals=GlobalFootprintProjection.Project(rotation,origin,
            localRayIntersections,0);
        var compared=0;var outside=0;var minimum=double.PositiveInfinity;
        var maximumPlaneDeviation=0d;
        // The projection above validates all traced local coordinates.
        // Classification below deliberately iterates every surviving ray.
        foreach(var local in localRayIntersections)
        {
            var x=origin[0]+rotation[0][0]*local.X+rotation[0][1]*local.Y+rotation[0][2]*local.Z;
            var y=origin[1]+rotation[1][0]*local.X+rotation[1][1]*local.Y+rotation[1][2]*local.Z;
            var z=origin[2]+rotation[2][0]*local.X+rotation[2][1]*local.Y+rotation[2][2]*local.Z;
            var relative=Subtract(new[]{x,y,z},plane.Origin);
            var offset=Math.Abs(Dot(relative,plane.Normal));
            if(!double.IsFinite(offset))throw new InvalidDataException("Nonfinite global mechanical plane residual.");
            maximumPlaneDeviation=Math.Max(maximumPlaneDeviation,offset);
            if(offset>planeTolerance)continue;
            compared++;
            var clearance=MechanicalFootprintBoundary.SignedClearance(
                plane.Vertices2D,Dot(relative,plane.AxisU),Dot(relative,plane.AxisV));
            if(clearance<0)outside++;
            minimum=Math.Min(minimum,clearance);
        }
        if(globals.SurvivingRayCount!=localRayIntersections.Count)
            throw new InvalidDataException("Global point projection changed the ray count.");
        return new Assessment(localRayIntersections.Count,compared,
            localRayIntersections.Count-compared,compared-outside,outside,
            compared==0?null:minimum,compared==0?null:(double)outside/compared,
            maximumPlaneDeviation,planeTolerance,"OpticStudio global XYZ (lens units)",
            "Planar user-measured CAD outline; projected signed EDGE margin only for already-surviving rays within planeTolerance of the outline plane. Out-of-plane rays are UNKNOWN, not blocked. A point at one LDE surface does not prove interception at another CAD plane. No physical clipping/throughput claim; use a ray-plane intersection analysis for noncoincident geometry.");
    }
}
