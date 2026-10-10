namespace ZemaxMCP.Server.Tools.Analysis;

/// <summary>Pure local-coordinate geometry supplied by the user. Does not
/// treat a Zemax semi-diameter as a declared mechanical aperture.</summary>
public static class MechanicalFootprintBoundary
{
    public sealed record Assessment(string Type, int SurvivingRayCount,
        int Inside, int Outside, double? MinimumSignedClearance,
        double? OutsideFractionOfSurvivors, string CoordinateFrame);

    internal static double[][] Rectangle(double[] b)
    {
        if (b == null || b.Length != 4 || b.Any(v => !double.IsFinite(v)) ||
            b[0] >= b[2] || b[1] >= b[3])
            throw new ArgumentException("Rectangle must be [xmin,ymin,xmax,ymax] in lens units.");
        return new[] { new[]{b[0],b[1]}, new[]{b[2],b[1]},
            new[]{b[2],b[3]}, new[]{b[0],b[3]} };
    }

    private static double Cross(double[] a,double[] b,double[] c) =>
        (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);
    private static bool OnSegment(double[] a,double[] b,double[] p) =>
        p[0]>=Math.Min(a[0],b[0]) && p[0]<=Math.Max(a[0],b[0]) &&
        p[1]>=Math.Min(a[1],b[1]) && p[1]<=Math.Max(a[1],b[1]);
    private static bool Crosses(double[] a,double[] b,double[] c,double[] d)
    {
        var x=Cross(a,b,c); var y=Cross(a,b,d);
        var z=Cross(c,d,a); var w=Cross(c,d,b);
        return (x==0 && OnSegment(a,b,c)) || (y==0 && OnSegment(a,b,d)) ||
            (z==0 && OnSegment(c,d,a)) || (w==0 && OnSegment(c,d,b)) ||
            ((x>0)!=(y>0) && (z>0)!=(w>0));
    }

    internal static void Validate(double[][] p)
    {
        if (p is not { Length: >= 3 and <= 64 } ||
            p.Any(v=>v==null || v.Length!=2 ||
                v.Any(x=>!double.IsFinite(x) || Math.Abs(x)>1e9)))
            throw new ArgumentException("Polygon must have 3..64 finite local [x,y] vertices.");
        var area=0d;
        for(var i=0;i<p.Length;i++)
        {
            var a=p[i];var b=p[(i+1)%p.Length];
            if(a[0]==b[0] && a[1]==b[1])
                throw new ArgumentException("Zero-length polygon edge.");
            area+=a[0]*b[1]-b[0]*a[1];
        }
        if(!double.IsFinite(area) || area==0)
            throw new ArgumentException("Polygon area must be nonzero.");
        for(var i=0;i<p.Length;i++)
        for(var j=i+2;j<p.Length;j++)
        {
            if(i==0 && j==p.Length-1) continue;
            if(Crosses(p[i],p[(i+1)%p.Length],p[j],p[(j+1)%p.Length]))
                throw new ArgumentException("Self-intersecting mechanical boundary.");
        }
    }

    internal static double SignedClearance(double[][] p,double x,double y)
    {
        var inside=false;var closest=double.PositiveInfinity;
        for(var i=0;i<p.Length;i++)
        {
            var a=p[i];var b=p[(i+1)%p.Length];
            var dx=b[0]-a[0];var dy=b[1]-a[1];
            var t=Math.Clamp(((x-a[0])*dx+(y-a[1])*dy)/(dx*dx+dy*dy),0,1);
            var ex=x-a[0]-t*dx;var ey=y-a[1]-t*dy;
            closest=Math.Min(closest,ex*ex+ey*ey);
            if ((a[1]>y)!=(b[1]>y) && x<(b[0]-a[0])*(y-a[1])/(b[1]-a[1])+a[0])
                inside=!inside;
        }
        var value=Math.Sqrt(closest);
        return value==0?0:inside?value:-value;
    }

    internal static Assessment Assess(double[][] polygon,
        IReadOnlyList<(double X,double Y)> survivingRays,string type)
    {
        Validate(polygon);
        if(survivingRays.Count==0)
            return new Assessment(type,0,0,0,null,null,"local LDE XY, lens units");
        var signed=survivingRays.Select(r=>SignedClearance(polygon,r.X,r.Y)).ToArray();
        var outside=signed.Count(d=>d<0);
        return new Assessment(type,signed.Length,signed.Length-outside,outside,
            signed.Min(),(double)outside/signed.Length,"local LDE XY, lens units");
    }
}
