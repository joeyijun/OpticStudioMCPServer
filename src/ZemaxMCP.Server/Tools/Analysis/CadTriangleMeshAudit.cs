namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>
/// Bounded two-sided finite ray-segment/opaque triangle-surface intersection.
/// A supplied triangle soup is NOT proven to be a closed watertight B-rep.
/// Only the earliest encountered opaque mesh surface is attributed per ray.
/// </summary>
public static class CadTriangleMeshAudit
{
    public sealed record Triangle(double[][] Vertices,int PartId);
    public sealed record PartReport(int PartId,int FirstBlockedRays);
    public sealed record Report(int SampledRays,int FirstBlockedRays,
        int UnblockedRays,int UnknownRays,int AmbiguousRays,int MissingRays,
        IReadOnlyList<PartReport> Parts,
        GlobalFootprintProjection.Point3? FirstCriticalHit,
        string Interpretation);

    private readonly record struct Vec(double X,double Y,double Z)
    {
        public static Vec operator +(Vec a,Vec b)=>new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
        public static Vec operator -(Vec a,Vec b)=>new(a.X-b.X,a.Y-b.Y,a.Z-b.Z);
        public static Vec operator *(Vec a,double t)=>new(a.X*t,a.Y*t,a.Z*t);
        public double Dot(Vec b)=>X*b.X+Y*b.Y+Z*b.Z;
        public Vec Cross(Vec b)=>new(Y*b.Z-Z*b.Y,Z*b.X-X*b.Z,X*b.Y-Y*b.X);
        public double Norm()=>Math.Sqrt(Dot(this));
        public GlobalFootprintProjection.Point3 Point()=>new(X,Y,Z);
    }
    private readonly record struct Face(Vec A,Vec E1,Vec E2,Vec Normal,int PartId);
    private enum Outcome {None,Hit,Ambiguous}

    private static Vec Read(double[] values)
    {
        if(values is not {Length:3} || values.Any(x=>!double.IsFinite(x)||Math.Abs(x)>1e9))
            throw new ArgumentException("Mesh vertices must be finite global XYZ within 1e9 lens units.");
        return new(values[0],values[1],values[2]);
    }

    private static Face[] Validate(IReadOnlyList<Triangle> triangles)
    {
        if(triangles is null || triangles.Count is <1 or >256)
            throw new ArgumentException("Mesh has to contain between 1 and 256 triangles.");
        var faces=new Face[triangles.Count];
        for(var i=0;i<triangles.Count;i++)
        {
            var triangle=triangles[i];
            if(triangle.PartId<1 || triangle.Vertices is not {Length:3})
                throw new ArgumentException("Each opaque mesh triangle needs a positive part ID and exactly three XYZ vertices.");
            var a=Read(triangle.Vertices[0]);
            var e1=Read(triangle.Vertices[1])-a;
            var e2=Read(triangle.Vertices[2])-a;
            var n=e1.Cross(e2);
            if(!double.IsFinite(n.Norm()) || n.Norm()<=1e-12*Math.Max(1d,e1.Norm()*e2.Norm()))
                throw new ArgumentException("Degenerate, zero-area or nonfinite mesh triangle.");
            faces[i]=new(a,e1,e2,n,triangle.PartId);
        }
        return faces;
    }

    private static Outcome Hit(Face face,Vec start,Vec end,
        out double fraction,out Vec position)
    {
        fraction=0;position=default;
        var direction=end-start;
        var cross=direction.Cross(face.E2);
        var denominator=face.E1.Dot(cross);
        var reference=Math.Max(1d,direction.Norm()*face.Normal.Norm());
        if(!double.IsFinite(denominator)||!double.IsFinite(reference))
            throw new InvalidDataException("Mesh intersection arithmetic overflow.");
        const double epsilon=1e-11;
        if(Math.Abs(denominator)<=epsilon*reference)
        {
            // Ray lies in a triangle's plane: cannot infer unique first
            // collision from a 2-D sheet without side/solid semantics.
            var distance=face.Normal.Dot(start-face.A);
            return Math.Abs(distance)<=epsilon*face.Normal.Norm() ?
                Outcome.Ambiguous : Outcome.None;
        }
        var fromVertex=start-face.A;
        var baryU=fromVertex.Dot(cross)/denominator;
        if(baryU < -epsilon || baryU>1+epsilon)return Outcome.None;
        var q=fromVertex.Cross(face.E1);
        var baryV=direction.Dot(q)/denominator;
        if(baryV < -epsilon || baryU+baryV>1+epsilon)return Outcome.None;
        var t=face.E2.Dot(q)/denominator;
        if(t<-epsilon||t>1+epsilon)return Outcome.None;
        fraction=Math.Clamp(t,0,1);
        position=start+direction*fraction;
        return Outcome.Hit;
    }

    internal static Report Assess(IReadOnlyList<Triangle> triangles,
        IReadOnlyList<IReadOnlyList<GlobalRaySegmentBoundary.Segment?>> rayPaths)
    {
        var mesh=Validate(triangles);
        if(rayPaths is null || rayPaths.Count>2601 ||
           (rayPaths.Count>0 && (rayPaths[0].Count is <1 or >23)) ||
           rayPaths.Any(path=>path==null||path.Count!=rayPaths[0].Count))
            throw new ArgumentException("Mesh path requires <=2601 pupil rays of equal-length 1..23 sequential LDE chords.");
        var counts=new Dictionary<int,int>();
        foreach(var f in mesh)counts.TryAdd(f.PartId,0);
        var blocked=0;var unknown=0;var ambiguous=0;var missing=0;var passed=0;
        GlobalFootprintProjection.Point3? firstHit=null;
        foreach(var path in rayPaths)
        {
            var wasBlocked=false;var wasUnknown=false;
            for(var i=0;i<path.Count;i++)
            {
                var chord=path[i];
                if(chord is null){missing++;wasUnknown=true;break;}
                var a=new Vec(chord.Start.X,chord.Start.Y,chord.Start.Z);
                var b=new Vec(chord.End.X,chord.End.Y,chord.End.Z);
                if(!double.IsFinite(a.Norm())||!double.IsFinite(b.Norm()))
                    throw new InvalidDataException("Nonfinite global optical chord for mesh.");
                if(i>0 && path[i-1] is { } previous)
                {
                    var prev=new Vec(previous.End.X,previous.End.Y,previous.End.Z);
                    if((prev-a).Norm()>1e-6*Math.Max(1d,a.Norm()))
                    {missing++;wasUnknown=true;break;}
                }
                if((b-a).Norm()<=1e-12){ambiguous++;wasUnknown=true;break;}
                var candidate=-1;var first=double.PositiveInfinity;
                Vec point=default;var coplanar=false;
                for(var j=0;j<mesh.Length;j++)
                {
                    var type=Hit(mesh[j],a,b,out var t,out var hit);
                    if(type==Outcome.Ambiguous){coplanar=true;continue;}
                    if(type==Outcome.Hit && t<first)
                    {candidate=j;first=t;point=hit;}
                }
                if(coplanar)
                {ambiguous++;wasUnknown=true;break;}
                if(candidate>=0)
                {
                    counts[mesh[candidate].PartId]++;
                    blocked++;wasBlocked=true;
                    firstHit??=point.Point();
                    break;
                }
            }
            if(!wasBlocked)
            {
                if(wasUnknown)unknown++;else passed++;
            }
        }
        if(blocked+unknown+passed!=rayPaths.Count)
            throw new InvalidDataException("Mesh ray attribution failed to partition rays.");
        return new Report(rayPaths.Count,blocked,passed,unknown,ambiguous,missing,
            counts.OrderBy(p=>p.Key).Select(x=>new PartReport(x.Key,x.Value)).ToArray(),
            firstHit,"User-declared OPAQUE, two-sided triangle-surface mesh in global XYZ. " +
            "Earliest finite ray/triangle intersection over actual consecutive LDE chords is counted ONCE per ray. " +
            "Triangles are not proven watertight, closed or calibrated CAD; coplanar segments, missing/discontinuous " +
            "optical paths and degenerate chords are UNKNOWN, never invented clipping or absorption. " +
            "No claim of whole-source throughput or physical NSC trace energy conservation.");
    }
}
