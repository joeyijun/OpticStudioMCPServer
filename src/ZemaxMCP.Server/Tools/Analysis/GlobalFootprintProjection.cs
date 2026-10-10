namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Transform actual local X/Y/Z ray intercepts (including surface
/// sag) using the LDE GetGlobalMatrix local-to-global vertex transform.</summary>
public static class GlobalFootprintProjection
{
    public sealed record Point3(double X, double Y, double Z);
    public sealed record Result(double[][] Rotation, double[] Origin,
        int SurvivingRayCount, Point3? Centroid, Point3? Minimum, Point3? Maximum,
        IReadOnlyList<Point3> SamplePoints, bool PointsTruncated,
        string CoordinateFrame);

    internal static Result Project(double[][] rotation, double[] origin,
        IReadOnlyList<(double X,double Y,double Z)> localIntersections, int sampleLimit)
    {
        if(rotation is not {Length:3} || rotation.Any(r=>r==null || r.Length!=3 ||
            r.Any(x=>!double.IsFinite(x))) ||
            origin is not {Length:3} || origin.Any(x=>!double.IsFinite(x)) ||
            sampleLimit is < 0 or > 128)
            throw new ArgumentException("Invalid finite 3x3 LDE rotation, vertex origin or bounded sample limit.");
        var all=new List<Point3>(localIntersections.Count);
        foreach(var p in localIntersections)
        {
            if(!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z))
                throw new InvalidDataException("Nonfinite local 3D ray intercept.");
            var x=origin[0]+rotation[0][0]*p.X+rotation[0][1]*p.Y+rotation[0][2]*p.Z;
            var y=origin[1]+rotation[1][0]*p.X+rotation[1][1]*p.Y+rotation[1][2]*p.Z;
            var z=origin[2]+rotation[2][0]*p.X+rotation[2][1]*p.Y+rotation[2][2]*p.Z;
            if(!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                throw new InvalidDataException("Global ray intercept overflows finite coordinate range.");
            all.Add(new Point3(x,y,z));
        }
        var label="OpticStudio LDE GetGlobalMatrix frame, lens units; includes traced local Z (surface sag)";
        if(all.Count==0)
            return new Result(rotation,origin,0,null,null,null,Array.Empty<Point3>(),false,label);
        return new Result(rotation,origin,all.Count,
            new Point3(all.Average(v=>v.X),all.Average(v=>v.Y),all.Average(v=>v.Z)),
            new Point3(all.Min(v=>v.X),all.Min(v=>v.Y),all.Min(v=>v.Z)),
            new Point3(all.Max(v=>v.X),all.Max(v=>v.Y),all.Max(v=>v.Z)),
            all.Take(sampleLimit).ToArray(),all.Count>sampleLimit,label);
    }
}
